using System;
using System.IO;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Nexos.Core;

public class FileSystemPlugin
{
    private bool ConfirmAction(string actionDescription)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n[AÇÃO SOLICITADA PELA IA] {actionDescription}. Permitir? (Y/n): ");
        Console.ResetColor();

        // Lê a tecla pressionada
        var key = Console.ReadKey(intercept: true);
        Console.WriteLine(key.KeyChar);

        // Se apertar Y (minúsculo ou maiúsculo) ou Enter, permite. Senão, bloqueia.
        return key.Key == ConsoleKey.Y || key.Key == ConsoleKey.Enter;
    }

    [KernelFunction, Description("Lê o conteúdo de um arquivo em um caminho específico.")]
    public async Task<string> ReadFileAsync(
        [Description("Caminho absoluto ou relativo do arquivo")] string filePath)
    {
        try
        {
            string absPath = Path.GetFullPath(filePath);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"[DEBUG C# PATH]: Tentando acessar {absPath}");
            Console.ResetColor();

            if (File.Exists(absPath))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n[Agent] Lendo arquivo: {absPath}");
                Console.ResetColor();
                return await File.ReadAllTextAsync(absPath);
            }
            return $"ERRO: Arquivo '{absPath}' não encontrado. Verifique o caminho e tente novamente.";
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO FATAL READ]: {ex}");
            Console.ResetColor();
            return $"ERRO FATAL: Falha ao ler arquivo. Verifique o caminho e tente novamente.";
        }
    }

    [KernelFunction, Description("Escreve ou sobrescreve conteúdo em um arquivo.")]
    public async Task<string> WriteFileAsync(
        [Description("Caminho do arquivo")] string filePath,
        [Description("O conteúdo a ser escrito")] string content)
    {
        if (!ConfirmAction($"Escrever/Sobrescrever arquivo '{filePath}'"))
        {
            return "Ação negada pelo usuário.";
        }

        try
        {
            string absPath = Path.GetFullPath(filePath);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"[DEBUG C# PATH]: Tentando acessar {absPath}");
            Console.ResetColor();

            var directory = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(absPath, content);
            return $"Sucesso: Conteúdo escrito em '{absPath}'.";
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO FATAL WRITE]: {ex}");
            Console.ResetColor();
            return $"ERRO FATAL: Falha ao escrever arquivo. Verifique o caminho e tente novamente.";
        }
    }

    [KernelFunction, Description("Deleta um arquivo especificado.")]
    public string DeleteFile(
        [Description("Caminho do arquivo a ser deletado")] string filePath)
    {
        if (!ConfirmAction($"Deletar o arquivo '{filePath}'"))
        {
            return "Ação negada pelo usuário.";
        }

        try
        {
            string absPath = Path.GetFullPath(filePath);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"[DEBUG C# PATH]: Tentando acessar {absPath}");
            Console.ResetColor();

            if (File.Exists(absPath))
            {
                File.Delete(absPath);
                return $"Sucesso: Arquivo '{absPath}' deletado.";
            }
            return $"ERRO: Arquivo '{absPath}' não encontrado. Verifique o caminho e tente novamente.";
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO FATAL DELETE]: {ex}");
            Console.ResetColor();
            return $"ERRO FATAL: Falha ao deletar arquivo. Verifique o caminho e tente novamente.";
        }
    }

    [KernelFunction, Description("Lista os arquivos e pastas de um diretório.")]
    public string ListDirectory(
        [Description("Caminho do diretório (ex: ./ ou /media/)")] string directoryPath)
    {
        try
        {
            string absPath = Path.GetFullPath(directoryPath);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"[DEBUG C# PATH]: Tentando acessar {absPath}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[Agent] Listando diretório: {absPath}");
            Console.ResetColor();

            if (Directory.Exists(absPath))
            {
                var files = Directory.GetFiles(absPath);
                var dirs = Directory.GetDirectories(absPath);
                
                return $"Diretórios:\n{string.Join("\n", dirs)}\n\nArquivos:\n{string.Join("\n", files)}";
            }
            return $"ERRO: Diretório '{absPath}' não encontrado. Verifique o caminho e tente novamente.";
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO FATAL LIST]: {ex}");
            Console.ResetColor();
            return $"ERRO FATAL: Caminho não encontrado. Verifique o caminho e tente novamente.";
        }
    }
}
