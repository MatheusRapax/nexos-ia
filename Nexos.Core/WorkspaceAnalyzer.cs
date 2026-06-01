using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Nexos.Core
{
    public static class WorkspaceAnalyzer
    {
        private static readonly string[] IgnoredFolders = { ".git", "bin", "obj", ".vs", "node_modules" };

        public static string GenerateRepoMap(string rootPath)
        {
            if (!Directory.Exists(rootPath))
            {
                return "O diretório informado não existe.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("<repo_map>");
            sb.AppendLine(Path.GetFileName(rootPath) + "/");
            GenerateTree(new DirectoryInfo(rootPath), "", sb);
            sb.AppendLine("</repo_map>");
            
            return sb.ToString();
        }

        private static void GenerateTree(DirectoryInfo dir, string indent, StringBuilder sb)
        {
            try
            {
                var dirs = dir.GetDirectories()
                              .Where(d => !IgnoredFolders.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                              .OrderBy(d => d.Name)
                              .ToList();
                              
                var files = dir.GetFiles()
                               .OrderBy(f => f.Name)
                               .ToList();

                int i = 0;
                int totalDirs = dirs.Count;
                foreach (var subDir in dirs)
                {
                    bool isLastDir = (i == totalDirs - 1) && (files.Count == 0);
                    sb.AppendLine($"{indent}{(isLastDir ? "└── " : "├── ")}{subDir.Name}/");
                    GenerateTree(subDir, indent + (isLastDir ? "    " : "│   "), sb);
                    i++;
                }

                i = 0;
                int totalFiles = files.Count;
                foreach (var file in files)
                {
                    bool isLastFile = (i == totalFiles - 1);
                    sb.AppendLine($"{indent}{(isLastFile ? "└── " : "├── ")}{file.Name}");
                    i++;
                }
            }
            catch (UnauthorizedAccessException)
            {
                sb.AppendLine($"{indent}└── [Acesso Negado]");
            }
        }
    }
}
