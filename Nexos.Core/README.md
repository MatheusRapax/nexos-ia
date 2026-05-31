# Nexos.Core — Controlador de Memória da IA Local

Projeto Console em **C# .NET 8** que usa o **Microsoft Semantic Kernel** para
orquestrar conversas com um **Llamafile** rodando localmente, com suporte a
memória persistente via **SQLite**.

---

## Pré-requisitos

| Ferramenta | Versão mínima |
|------------|---------------|
| .NET SDK   | 8.0           |
| Llamafile  | qualquer      |

---

## Configuração rápida

### 1. Instalar o .NET 8 SDK (se necessário)

```bash
# Ubuntu / Debian
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0
```

### 2. Criar e configurar o projeto

```bash
# Entre na pasta do projeto
cd /media/matheus/Nexos/brain/Nexos.Core

# Restaure / instale os pacotes NuGet
dotnet restore

# --- OU, se quiser criar do zero a partir da CLI: ---

dotnet new console -n Nexos.Core --framework net8.0
cd Nexos.Core

dotnet add package Microsoft.SemanticKernel --version 1.21.1
dotnet add package Microsoft.SemanticKernel.Connectors.OpenAI --version 1.21.1
dotnet add package Microsoft.SemanticKernel.Plugins.Memory.Sqlite --version 1.21.1-preview
```

### 3. Iniciar o Llamafile

```bash
# Exemplo com LLaMA 3.2 (ajuste o .llamafile conforme seu modelo)
./llama-3.2-1b-instruct.llamafile --server --port 8080 --host 0.0.0.0
```

### 4. Executar o Nexos.Core

```bash
dotnet run
```

---

## Estrutura do projeto

```
Nexos.Core/
├── Program.cs          # Loop principal + integração Semantic Kernel
├── Nexos.Core.csproj   # Definição do projeto e pacotes NuGet
├── .gitignore
└── nexos_memory.db     # (gerado em runtime) Banco de memória SQLite
```

---

## Pacotes NuGet utilizados

| Pacote | Versão | Finalidade |
|--------|--------|------------|
| `Microsoft.SemanticKernel` | 1.21.1 | Núcleo do orquestrador de IA |
| `Microsoft.SemanticKernel.Connectors.OpenAI` | 1.21.1 | Chat completion via API OpenAI-compatible |
| `Microsoft.SemanticKernel.Plugins.Memory.Sqlite` | 1.21.1-preview | Memória vetorial persistida em SQLite |

---

## Arquitetura

```
Console (usuário)
      │
      ▼
  Program.cs
      │  ChatHistory (memória RAM — sessão atual)
      │
      ▼
Semantic Kernel
      │  IChatCompletionService
      │
      ▼
OpenAI Connector
      │  HTTP POST /v1/chat/completions
      │
      ▼
Llamafile (localhost:8080)
      │
      ▼
  Modelo LLM local (LLaMA, Mistral, etc.)
```

---

## Próximos passos

- [ ] Integrar `SqliteMemoryStore` para memória vetorial de longo prazo
- [ ] Criar plugins (Skills) customizados para o Nexos
- [ ] Adicionar embeddings locais com `Microsoft.SemanticKernel.Connectors.Ollama`
- [ ] Implementar RAG (Retrieval-Augmented Generation) com arquivos locais
