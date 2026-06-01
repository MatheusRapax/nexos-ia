// ============================================================
//  Nexos.Core — Smart Router v2 (Fase 1b)
//
//  Fix v2:
//    • ResolveCase: path case-insensitive no Linux
//    • DetectIntent: bare path (ex: /algum/caminho) → list ou read
//    • Sem caminho detectado → LLM usa histórico para responder
// ============================================================

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Nexos.Core;

// ═══════════════════════════════════════════════════════════
// CONFIGURAÇÃO
// ═══════════════════════════════════════════════════════════

const string ModelId  = "LLaMA_CPP";
const string Endpoint = "http://localhost:8080/v1";
const string ApiKey   = "sk-no-key-required";

var httpClient = new HttpClient(new LlamafileCompatHandler(new HttpClientHandler()))
{
    Timeout = TimeSpan.FromSeconds(600)
};

var builder = Kernel.CreateBuilder();
builder.AddOpenAIChatCompletion(
    modelId:    ModelId,
    endpoint:   new Uri(Endpoint),
    apiKey:     ApiKey,
    httpClient: httpClient
);

var kernel = builder.Build();
var chatService = kernel.GetRequiredService<IChatCompletionService>();

var settings = new OpenAIPromptExecutionSettings
{
    Temperature = 0.1,
    TopP        = 0.9,
    MaxTokens   = 4000
};

var fs = new FileSystemPlugin();

// ═══════════════════════════════════════════════════════════
// HISTÓRICO
// ═══════════════════════════════════════════════════════════

var history = new ChatHistory();
string currentPlan = "";

history.AddSystemMessage(
    "Você é o Nexos, assistente técnico sênior autônomo. " +
    "Você tem acesso ao <repo_map>, que mostra a estrutura atual do projeto. Use-o para se localizar. Só utilize o [COMANDO: LER nome_do_arquivo.ext] se precisar ver o código interno de um arquivo específico.\n" +
    "Se precisar buscar informações no RAG de memória, responda APENAS: [COMANDO: MCP buscar 'assunto']\n" +
    "O sistema executará o comando e injetará a resposta para você continuar pensando em loop.\n" +
    "Trabalhe passo a passo.\n" +
    "MUITO IMPORTANTE: Para tarefas complexas, sua PRIMEIRA AÇÃO no turno 1 deve ser criar um plano usando [COMANDO: PLANO <checklist_markdown>]. " +
    "Exemplo: [COMANDO: PLANO - [ ] Criar pasta \\n - [ ] Mover arquivo]. " +
    "Em seguida, EXECUTE as ações reais silenciosamente usando [COMANDO: TERMINAL ...] ou [COMANDO: SALVAR ...].\n" +
    "- Para criar/editar: gere [COMANDO: SALVAR caminho/arquivo.ext] seguido do bloco de código. (Gere APENAS UM arquivo por vez, aguarde confirmação).\n" +
    "- Para terminal (ex: criar pasta, mover arquivo, dotnet build): use [COMANDO: TERMINAL <comando>]. A resposta do terminal retornará a você.\n" +
    "Aguarde a confirmação do sistema e o log do terminal antes de planejar o próximo arquivo/comando. " +
    "Quando TODAS as tarefas estiverem concluídas, atualize seu plano marcando as etapas com [x] e só então emita a tag [COMANDO: FINALIZAR]."
);

// ═══════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════

// Resolve caminho com case-insensitive no Linux
// Ex: /media/matheus/nexos → /media/matheus/Nexos
static string ResolveCase(string path, string currentDir = "")
{
    if (!string.IsNullOrEmpty(currentDir) && !Path.IsPathRooted(path) && !path.StartsWith("/") && !path.StartsWith("~/"))
    {
        path = Path.Combine(currentDir, path);
    }

    if (OperatingSystem.IsWindows()) return path;

    if (Directory.Exists(path) || File.Exists(path)) return path;

    var parts = path.TrimEnd('/').Split('/');
    var current = "/";

    for (int i = 1; i < parts.Length; i++)
    {
        if (string.IsNullOrEmpty(parts[i])) continue;
        if (!Directory.Exists(current)) return path;

        var match = Directory
            .GetFileSystemEntries(current)
            .FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e), parts[i],
                    StringComparison.OrdinalIgnoreCase));

        if (match is null) return path;
        current = match;
    }

    return current;
}

// Detecta intenção e extrai caminho (custo zero de tokens)
static (string intent, string? path) DetectIntent(string input, string currentDir = "")
{
    var lower = input.ToLowerInvariant();
    var trimmed = input.Trim();

    bool IsPathStart(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.StartsWith('/') || s.StartsWith("./") || s.StartsWith("~/") || s.StartsWith(".\\")) return true;
        if (s.StartsWith('.') && s.Length > 1) return true;
        if (s.Contains('.') && s.Length > 2) return true;
        if (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':') return true;
        return false;
    }

    // Bare path: input é apenas um caminho (resposta a uma pergunta anterior)
    // Ex: /media/matheus/Nexos/  ou  ./src  ou  C:\users\...
    if (!trimmed.Contains(' ') && IsPathStart(trimmed))
    {
        var p = trimmed.Trim('"', '\'');
        var resolved = ResolveCase(p, currentDir);
        return (Directory.Exists(resolved) ? "list" : "read", p);
    }

    bool isChatTask = lower.Contains("escreva") || lower.Contains("crie ") || lower.Contains("criar ") || 
                      lower.Contains("gere ") || lower.Contains("gerar ") || lower.Contains("código") || 
                      lower.Contains("codigo") || lower.Contains("script") || lower.Contains("salve ") || 
                      lower.Contains("salvar ") || lower.Contains("explique") || lower.Contains("como ");

    if (isChatTask) return ("chat", null);

    bool isList = lower.Contains("list") || lower.Contains(" ls ")  ||
                  lower.Contains("diretório") || lower.Contains("diretorio") ||
                  lower.Contains("pasta")    || lower.Contains("arquivos de") ||
                  lower.Contains("arquivos do") || lower.Contains("conteúdo de");

    bool isRead = lower.Contains("ler ") || lower.Contains("leia") ||
                  lower.Contains("mostrar") || lower.Contains("ver o arquivo") ||
                  lower.Contains("abrir");

    string? extractedPath = null;
    foreach (var word in trimmed.Split(' ', '\t'))
    {
        var w = word.Trim('"', '\'', ',', '.');
        if (IsPathStart(w))
        {
            extractedPath = w;
            break;
        }
    }

    if (isList) return ("list",  extractedPath);
    if (isRead) return ("read",  extractedPath);
    return          ("chat",  null);
}

// ═══════════════════════════════════════════════════════════
// BANNER
// ═══════════════════════════════════════════════════════════

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   NEXOS — Smart Router v2 (Fase 1b)          ║");
Console.WriteLine("║   File ops → saída direta  │  Chat → LLM     ║");
Console.WriteLine("║   Timeout: 600s  │  'sair' para encerrar     ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// LOOP PRINCIPAL
// ═══════════════════════════════════════════════════════════

string currentContextDir = Directory.GetCurrentDirectory();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("Você: ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(input)) continue;

    if (input.Equals("sair", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("exit",  StringComparison.OrdinalIgnoreCase))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\nNexos: Até mais!");
        Console.ResetColor();
        break;
    }

    var (intent, rawPath) = DetectIntent(input, currentContextDir);

    // FILE SYSTEM: executa direto, exibe direto
    if (intent is "list" or "read" && rawPath is not null)
    {
        var resolvedPath = ResolveCase(rawPath, currentContextDir);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        string toolResult;

        if (intent == "list")
        {
            Console.WriteLine($"  [Tool] ListDirectory({resolvedPath})");
            toolResult = fs.ListDirectory(resolvedPath);
            if (!toolResult.StartsWith("ERRO"))
            {
                currentContextDir = resolvedPath;
            }
        }
        else
        {
            Console.WriteLine($"  [Tool] ReadFile({resolvedPath})");
            toolResult = await fs.ReadFileAsync(resolvedPath);
        }
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write("\nNexos: ");
        Console.ResetColor();
        Console.WriteLine(toolResult);
        Console.WriteLine();

        // Salva no histórico para o LLM ter contexto nas perguntas seguintes
        history.AddUserMessage(input);
        history.AddAssistantMessage($"Executei '{intent}' em '{resolvedPath}':\n{toolResult}");

        continue;
    }

    // CHAT: envia ao LLM (inclui perguntas sobre resultados anteriores)
    history.AddUserMessage(input);

    bool agentFinished = false;
    while (!agentFinished)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [LLM] Pensando...");
        Console.ResetColor();

        try
        {
            // Injeção dinâmica do Repo Map
            var repoMapText = WorkspaceAnalyzer.GenerateRepoMap(currentContextDir);
            if (!string.IsNullOrWhiteSpace(currentPlan))
            {
                repoMapText += $"\n\n<seu_plano_atual>\n{currentPlan}\n</seu_plano_atual>";
            }
            var repoMapMsg = new ChatMessageContent(AuthorRole.System, repoMapText);
            history.Add(repoMapMsg);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var response = await chatService.GetChatMessageContentAsync(
                history,
                executionSettings: settings,
                kernel: kernel
            );

            sw.Stop();
            history.Remove(repoMapMsg); // Remove para não poluir o contexto longo

            var text = response.Content ?? string.Empty;

            int promptTokens = 0, completionTokens = 0, totalTokens = 0;
            if (response.Metadata != null && response.Metadata.TryGetValue("Usage", out var usageObj) && usageObj != null)
            {
                var usageProps = usageObj.GetType().GetProperties();
                var totalProp = usageProps.FirstOrDefault(p => p.Name.Contains("TotalToken"));
                var compProp = usageProps.FirstOrDefault(p => p.Name.Contains("CompletionToken") || p.Name.Contains("OutputToken"));
                var promptProp = usageProps.FirstOrDefault(p => p.Name.Contains("PromptToken") || p.Name.Contains("InputToken"));

                if (totalProp != null) totalTokens = Convert.ToInt32(totalProp.GetValue(usageObj) ?? 0);
                if (compProp != null) completionTokens = Convert.ToInt32(compProp.GetValue(usageObj) ?? 0);
                if (promptProp != null) promptTokens = Convert.ToInt32(promptProp.GetValue(usageObj) ?? 0);
            }
            double seconds = sw.Elapsed.TotalSeconds;
            double tps = seconds > 0 ? (completionTokens / seconds) : 0;
            
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"  [⏱️  {sw.Elapsed.Minutes}m {sw.Elapsed.Seconds}s | 🪙 Tokens: {totalTokens} (P: {promptTokens}, R: {completionTokens}) | ⚡ {tps:F1} t/s]");
            Console.ResetColor();

            if (string.IsNullOrWhiteSpace(text))
            {
                if (history.Count > 1) history.RemoveAt(history.Count - 1);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Nexos: (resposta vazia — reformule a pergunta)\n");
                Console.ResetColor();
                break;
            }

            // EXTRAI TODOS OS COMANDOS SEQUENCIALMENTE
            var commandsMatches = System.Text.RegularExpressions.Regex.Matches(text, @"\[COMANDO:\s*([A-Z_]+)(?:\s+(.*?))?\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            if (commandsMatches.Count > 0)
            {
                history.AddAssistantMessage(text);
                var stringBuilderSysMsg = new System.Text.StringBuilder();

                foreach (System.Text.RegularExpressions.Match match in commandsMatches)
                {
                    var cmdType = match.Groups[1].Value.Trim().ToUpper();
                    var cmdArg = match.Groups[2].Value.Trim();
                    
                    if (cmdType == "FINALIZAR")
                    {
                        var cleanText = text.Replace(match.Value, "").Trim();
                        cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"\[COMANDO:.*?\]", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
                        
                        if (!string.IsNullOrEmpty(cleanText))
                        {
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.Write("\nNexos: ");
                            Console.ResetColor();
                            Console.WriteLine(cleanText);
                        }
                        
                        if (!string.IsNullOrWhiteSpace(currentPlan) && currentPlan.Contains("[ ]"))
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"\n[Sistema] Comando FINALIZAR negado. O seu plano ainda contém tarefas desmarcadas '[ ]'.");
                            Console.ResetColor();
                            stringBuilderSysMsg.AppendLine($"[Sistema] Comando FINALIZAR negado. O seu plano ainda contém tarefas desmarcadas '[ ]'. Execute o que falta e atualize o plano marcando com '[x]'.");
                            continue;
                        }

                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine("\n[Agente Executando] Tarefa Finalizada.");
                        Console.ResetColor();
                        agentFinished = true;
                        break;
                    }
                    else if (cmdType == "PLANO")
                    {
                        currentPlan = cmdArg;
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"  [Tracker] Bloco de Notas atualizado.");
                        Console.ResetColor();
                        stringBuilderSysMsg.AppendLine($"[Sistema] Plano atualizado com sucesso. Continue a execução.");
                    }
                    else if (cmdType == "SALVAR")
                    {
                        var savePath = ResolveCase(cmdArg, currentContextDir);
                        
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($"  [Agente] Escrevendo: {savePath}");
                        Console.ResetColor();
                        
                        string contentToSave = string.Empty;
                        int startIndex = match.Index + match.Length;
                        var codeMatch = System.Text.RegularExpressions.Regex.Match(text.Substring(startIndex), @"```(?:.*?)\r?\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline);
                        if (codeMatch.Success) {
                            contentToSave = codeMatch.Groups[1].Value.TrimEnd();
                        }

                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write($"\n⚠️ Permitir que a IA salve o arquivo {savePath}? (S/N): ");
                        Console.ResetColor();

                        if (Console.ReadLine()?.Trim().ToUpper() == "S")
                        {
                            try
                            {
                                var dir = System.IO.Path.GetDirectoryName(savePath);
                                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                                    System.IO.Directory.CreateDirectory(dir);
                                System.IO.File.WriteAllText(savePath, contentToSave);
                                
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[Sucesso] Arquivo salvo: {savePath}\n");
                                Console.ResetColor();

                                stringBuilderSysMsg.AppendLine($"[Sistema] Arquivo {savePath} salvo com sucesso.");
                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[Erro] Falha ao salvar: {ex.Message}\n");
                                Console.ResetColor();
                                stringBuilderSysMsg.AppendLine($"[Sistema] Erro ao salvar o arquivo: {ex.Message}.");
                            }
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"[Cancelado] O arquivo {savePath} não foi salvo.\n");
                            Console.ResetColor();
                            stringBuilderSysMsg.AppendLine($"[Sistema] Usuário rejeitou salvamento de {savePath}.");
                        }
                    }
                    else if (cmdType == "TERMINAL")
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write($"\n⚠️ Permitir que a IA execute no terminal: '{cmdArg}'? (S/N): ");
                        Console.ResetColor();

                        if (Console.ReadLine()?.Trim().ToUpper() == "S")
                        {
                            try
                            {
                                var psi = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "powershell.exe" : "/bin/bash",
                                    Arguments = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? $"-NoProfile -Command \"{cmdArg}\"" : $"-c \"{cmdArg}\"",
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    WorkingDirectory = currentContextDir
                                };

                                using var proc = System.Diagnostics.Process.Start(psi);
                                if (proc != null)
                                {
                                    proc.WaitForExit();
                                    
                                    string outLog = proc.StandardOutput.ReadToEnd().Trim();
                                    string errLog = proc.StandardError.ReadToEnd().Trim();

                                    var finalLog = "";
                                    if (!string.IsNullOrEmpty(outLog)) finalLog += $"Saída:\n{outLog}\n";
                                    if (!string.IsNullOrEmpty(errLog)) finalLog += $"Erro:\n{errLog}\n";
                                    if (string.IsNullOrEmpty(finalLog)) finalLog = "Comando executado sem saída visível.";
                                    
                                    if (proc.ExitCode != 0 && errLog.Contains("Move-Item"))
                                    {
                                        finalLog += "\n[DICA DO CÉREBRO]: O comando 'mv' no PowerShell falha com múltiplos arquivos. Use wildcards (ex: mv *.html pasta/) ou repita o comando para cada arquivo.";
                                    }

                                    Console.ForegroundColor = proc.ExitCode == 0 ? ConsoleColor.Green : ConsoleColor.Red;
                                    Console.WriteLine($"[Terminal Exit {proc.ExitCode}] Executado.");
                                    Console.ResetColor();
                                    
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.WriteLine(finalLog);
                                    Console.ResetColor();

                                    stringBuilderSysMsg.AppendLine($"[Sistema] Resultado TERMINAL '{cmdArg}' (Exit: {proc.ExitCode}):\n{finalLog}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[Erro Terminal] {ex.Message}\n");
                                Console.ResetColor();
                                stringBuilderSysMsg.AppendLine($"[Sistema] Erro TERMINAL '{cmdArg}': {ex.Message}");
                            }
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"[Cancelado] Comando abortado.\n");
                            Console.ResetColor();
                            stringBuilderSysMsg.AppendLine($"[Sistema] Usuário negou a execução TERMINAL de '{cmdArg}'.");
                        }
                    }
                    else if (cmdType == "LISTAR")
                    {
                        var path = ResolveCase(cmdArg, currentContextDir);
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($"  [Agente] Listando: {path}");
                        Console.ResetColor();
                        
                        var result = fs.ListDirectory(path);
                        stringBuilderSysMsg.AppendLine($"[Sistema] Resultado LISTAR {path}:\n{result}");
                    }
                    else if (cmdType == "LER")
                    {
                        var path = ResolveCase(cmdArg, currentContextDir);
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($"  [Agente] Lendo: {path}");
                        Console.ResetColor();
                        
                        var result = await fs.ReadFileAsync(path);
                        stringBuilderSysMsg.AppendLine($"[Sistema] Resultado LER {path}:\n{result}");
                    }
                    else if (cmdType == "MCP")
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($"  [Agente] MCP Query: {cmdArg}");
                        Console.ResetColor();
                        stringBuilderSysMsg.AppendLine($"[Sistema] Resultado MCP: O servidor MCP ainda não está conectado.");
                    }
                }
                
                if (!agentFinished && stringBuilderSysMsg.Length > 0)
                {
                    history.AddSystemMessage(stringBuilderSysMsg.ToString());
                }
                else if (!agentFinished)
                {
                    history.AddSystemMessage("[Sistema] Comando não reconhecido ou sem saída válida.");
                }

                continue;
            }

            // SE NÃO EXECUTOU COMANDOS, IMPRIME COMO CHAT
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("\nNexos: ");
            Console.ResetColor();
            Console.WriteLine(text);
            Console.WriteLine();

            history.AddAssistantMessage(text);
            
            // Para não prender o usuário num loop eterno se a IA apenas 
            // responder sem usar [COMANDO: FINALIZAR] ou comandos válidos.
            if (!text.Contains("[COMANDO:")) 
            {
                agentFinished = true;
                break;
            }
    }
    catch (TaskCanceledException)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n[TIMEOUT] Modelo ultrapassou 600s. Reinicie para limpar o contexto.\n");
        Console.ResetColor();
        if (history.Count > 1) history.RemoveAt(history.Count - 1);
        break;
    }
    catch (HttpRequestException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[CONEXÃO] Servidor inacessível: {ex.Message}\n");
        Console.ResetColor();
        if (history.Count > 1) history.RemoveAt(history.Count - 1);
        break;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[ERRO] {ex.GetType().Name}: {ex.Message}\n");
        Console.ResetColor();
        if (history.Count > 1) history.RemoveAt(history.Count - 1);
        break;
    }
    }
}

// ═══════════════════════════════════════════════════════════
// HANDLER: remove stream_options incompatível com llama.cpp
// ═══════════════════════════════════════════════════════════

public class LlamafileCompatHandler : DelegatingHandler
{
    public LlamafileCompatHandler(HttpMessageHandler inner) : base(inner) {}

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content != null &&
            request.RequestUri?.ToString().Contains("chat/completions") == true)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("\"stream_options\":{\"include_usage\":true}"))
            {
                body = body.Replace(",\"stream_options\":{\"include_usage\":true}", "");
                request.Content = new StringContent(
                    body, System.Text.Encoding.UTF8, "application/json");
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
