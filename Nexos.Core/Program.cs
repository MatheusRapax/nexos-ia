// ============================================================
//  Nexos.Core — Controlador de Memória com RAG (Long-Term Memory)
//
//  Pipeline de raciocínio a cada turno:
//    1. Captura input do usuário
//    2. Busca memórias relevantes no SQLite (embedding search)
//    3. Constrói prompt aumentado com contexto histórico
//    4. Envia ao Llamafile e exibe a resposta em streaming
//    5. Salva interação de volta no SQLite para enriquecer a base
// ============================================================

// Suprime avisos de APIs experimentais do Semantic Kernel em tempo de compilação.
// As APIs de memória (ISemanticTextMemory, SqliteMemoryStore) ainda são marcadas
// como [Experimental] na versão atual, mas são estáveis o suficiente para uso.
#pragma warning disable SKEXP0001 // ISemanticTextMemory
#pragma warning disable SKEXP0010 // AddOpenAITextEmbeddingGeneration
#pragma warning disable SKEXP0020 // SqliteMemoryStore
#pragma warning disable SKEXP0050 // SemanticTextMemory / TextMemoryPlugin

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Connectors.Sqlite;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Memory;

// ═══════════════════════════════════════════════════════════════
// CONSTANTES DE CONFIGURAÇÃO
// ═══════════════════════════════════════════════════════════════

const string ModelId       = "LLaMA_CPP";
const string EndpointUrl   = "http://localhost:8080/v1";
const string ApiKey        = "sk-no-key-required";
const string CollectionName = "NexosMemory";   // Nome da coleção/tabela no SQLite
const int    MemoryTopK    = 2;                // REDUZIDO PARA 2: Acelera o processamento no processador local
const double MinRelevance  = 0.60;             // Relevância mínima (0.0 a 1.0)

// ═══════════════════════════════════════════════════════════════
// 1. CONSTRUÇÃO DO KERNEL
// ═══════════════════════════════════════════════════════════════

var builder = Kernel.CreateBuilder();

// Interceptador para remover parâmetros incompatíveis com o Llamafile local
var httpClient = new HttpClient(new LlamafileCompatHandler(new HttpClientHandler()));

// Serviço de Chat Completion — aponta ao Llamafile local.
// O Llamafile implementa a API da OpenAI, então este conector funciona direto.
builder.AddOpenAIChatCompletion(
    modelId:  ModelId,
    endpoint: new Uri(EndpointUrl),
    apiKey:   ApiKey,
    httpClient: httpClient
);

// Serviço de Text Embedding Generation — usado para vetorizar textos
// antes de salvá-los e para vetorizar a query antes de buscá-la.
// NOTA: Certifique-se que seu Llamafile suporta /v1/embeddings.
// Se não suportar, a linha abaixo lançará HttpRequestException na primeira busca.
// O overload com endpoint customizado usa um HttpClient configurado manualmente.
builder.AddOpenAITextEmbeddingGeneration(
    modelId:    ModelId,
    apiKey:     ApiKey,
    httpClient: new HttpClient { BaseAddress = new Uri(EndpointUrl) }
);

var kernel = builder.Build();

// ═══════════════════════════════════════════════════════════════
// 2. MEMÓRIA DE LONGO PRAZO — SQLite + Embeddings
// ═══════════════════════════════════════════════════════════════

// Caminho do banco: mesma pasta do executável, para portabilidade.
var dbPath = Path.Combine(AppContext.BaseDirectory, "nexos_memory.db");

// SqliteMemoryStore persiste vetores (embeddings) no SQLite local.
// ConnectAsync cria o arquivo se não existir.
var memoryStore = await SqliteMemoryStore.ConnectAsync(dbPath);

// Recupera o serviço de embedding registrado no Kernel.
var embeddingGenerator = kernel.GetRequiredService<ITextEmbeddingGenerationService>();

// ISemanticTextMemory combina o store (onde salvar) com o gerador (como vetorizar).
// É a interface principal de memória semântica do Semantic Kernel.
ISemanticTextMemory memory = new SemanticTextMemory(memoryStore, embeddingGenerator);

// Garante que a coleção de memórias existe no banco.
// Se já existir, não faz nada.
try
{
    await memoryStore.CreateCollectionAsync(CollectionName);
}
catch
{
    // A coleção já existe — podemos ignorar este erro com segurança.
}

// ═══════════════════════════════════════════════════════════════
// 3. CONFIGURAÇÕES DE INFERÊNCIA E SERVIÇOS
// ═══════════════════════════════════════════════════════════════

var chatService = kernel.GetRequiredService<IChatCompletionService>();

// ═══════════════════════════════════════════════════════════════
// 4. BANNER DO CONSOLE
// ═══════════════════════════════════════════════════════════════

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║        NEXOS — Sistema de Memória RAG Local          ║");
Console.WriteLine("║  Modelo: LLaMA_CPP  │  Memória: SQLite (Long-Term)  ║");
Console.WriteLine("║  Digite 'sair' para encerrar.                        ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine($"  📂 Banco de memória: {dbPath}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════
// 5. LOOP PRINCIPAL — Pipeline RAG
// ═══════════════════════════════════════════════════════════════

while (true)
{
    // ── A) CAPTURA DO INPUT ──────────────────────────────────────
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("Você: ");
    Console.ResetColor();

    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(userInput))
        continue;

    if (userInput.Equals("sair", StringComparison.OrdinalIgnoreCase) ||
        userInput.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\nNexos: Memória salva. Até a próxima!");
        Console.ResetColor();
        break;
    }

    // ── B) BUSCA DE MEMÓRIAS RELEVANTES (RAG Retrieval) ─────────
    // Vetorizamos o input do usuário e buscamos as memórias mais
    // semanticamente próximas no SQLite.

    var memoriesFound    = new List<string>();
    var memoriesDebug    = new List<string>();
    var memorySearchFailed = false;

    try
    {
        await foreach (var result in memory.SearchAsync(
            collection: CollectionName,
            query:       userInput,
            limit:       MemoryTopK,
            minRelevanceScore: MinRelevance))
        {
            memoriesFound.Add(result.Metadata.Text);
            memoriesDebug.Add($"  [{result.Relevance:P0}] {result.Metadata.Text[..Math.Min(80, result.Metadata.Text.Length)]}...");
        }
    }
    catch (Exception ex)
    {
        // Falha na busca vetorial (ex: endpoint /embeddings não suportado).
        // Degradamos graciosamente: continuamos sem memória de longo prazo.
        memorySearchFailed = true;
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"  ⚠ Busca de memória indisponível: {ex.Message}");
        Console.WriteLine("  → Continuando sem contexto histórico (modo básico).");
        Console.ResetColor();
    }

    // Mostra quais memórias foram recuperadas (modo debug visual)
    if (memoriesFound.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  🧠 {memoriesFound.Count} memória(s) recuperada(s):");
        foreach (var dbg in memoriesDebug)
            Console.WriteLine(dbg);
        Console.ResetColor();
    }

    // ── C) CONSTRUÇÃO DO PROMPT AUMENTADO E HISTÓRICO ───────────
    
    // Instancia um novo histórico a cada turno (Stateless com RAG)
    var history = new ChatHistory(
        "Você é o Nexos, um Arquiteto de Software Sênior, agnóstico a linguagens e " +
        "especializado em sistemas on-premise de alta performance. " +
        "Comunique-se de forma natural, direta e em texto plano."
    );

    string userMessage;

    if (memoriesFound.Count > 0)
    {
        var contextBlock = string.Join("\n", memoriesFound);

        userMessage = 
            "--- CONTEXTO HISTÓRICO ---\n" +
            $"{contextBlock}\n" +
            "--------------------------\n\n" +
            "Responda de forma natural e conversacional à seguinte interação do usuário (use o contexto acima apenas se for relevante):\n" +
            $"{userInput}";
    }
    else
    {
        userMessage = userInput;
    }

    // Adiciona a mensagem do usuário formatada
    history.AddUserMessage(userMessage);

    // ── D) CHAMADA AO MODELO + RESPOSTA EM STREAMING ────────────
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.Write("\nNexos: ");
    Console.ResetColor();

    var fullResponse = new System.Text.StringBuilder();

    // Configurações rígidas de inferência para evitar alucinações/loops de repetição
    var settings = new OpenAIPromptExecutionSettings
    {
        Temperature = 0.1,         // Deixa o modelo focado e extremamente lógico
        TopP = 0.9,
        MaxTokens = 1500
    };

    try
    {
        await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(
            history,
            executionSettings: settings,
            kernel: kernel))
        {
            Console.Write(chunk.Content);
            fullResponse.Append(chunk.Content);
        }

        Console.WriteLine("\n");
    }
    catch (HttpRequestException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[ERRO DE CONEXÃO] Não foi possível alcançar o Llamafile.");
        Console.WriteLine($"Verifique se o servidor está rodando em {EndpointUrl}");
        Console.WriteLine($"Detalhe: {ex.Message}\n");
        Console.ResetColor();

        continue;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[ERRO] {ex.GetType().Name}: {ex.Message}\n");
        Console.ResetColor();

        continue;
    }

    var responseText = fullResponse.ToString();

    // ── E) PERSISTÊNCIA NA MEMÓRIA DE LONGO PRAZO (SQLite) ──────
    // Salvamos dois registros distintos:
    //   1. O que o usuário disse (input)
    //   2. O que a IA respondeu (output)
    // Cada registro recebe um ID único baseado em timestamp + GUID parcial.

    if (!memorySearchFailed)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var uid       = Guid.NewGuid().ToString("N")[..8]; // 8 chars únicos

            // Salva o input do usuário
            await memory.SaveInformationAsync(
                collection: CollectionName,
                text:        $"Usuário disse: {userInput}",
                id:          $"user-{timestamp}-{uid}",
                description: "Mensagem do usuário"
            );

            // Salva a resposta da IA — útil para o modelo "saber" o que já respondeu
            await memory.SaveInformationAsync(
                collection: CollectionName,
                text:        $"Nexos respondeu: {responseText}",
                id:          $"nexos-{timestamp}-{uid}",
                description: "Resposta da IA"
            );

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  💾 Memória salva no SQLite.\n");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            // Falha ao salvar não deve interromper a conversa
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  ⚠ Falha ao persistir memória: {ex.Message}\n");
            Console.ResetColor();
        }
    }
}

#pragma warning restore SKEXP0001
#pragma warning restore SKEXP0010
#pragma warning restore SKEXP0020
#pragma warning restore SKEXP0050

// Classe para interceptar requests HTTP e remover parâmetros que quebram o Llamafile local
public class LlamafileCompatHandler : DelegatingHandler
{
    public LlamafileCompatHandler(HttpMessageHandler innerHandler) : base(innerHandler) {}

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Intercepta apenas as requisições de chat
        if (request.Content != null && request.RequestUri != null && request.RequestUri.ToString().Contains("chat/completions"))
        {
            var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            
            // Remove a opção "stream_options" que o SK envia por padrão e quebra o Llama.cpp local
            if (requestBody.Contains("\"stream_options\":{\"include_usage\":true}"))
            {
                requestBody = requestBody.Replace(",\"stream_options\":{\"include_usage\":true}", "");
                // Atualiza o Content da requisição com o JSON corrigido
                request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
