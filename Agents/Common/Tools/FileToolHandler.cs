using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentSharp;
using Click;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class FileToolHandler : IToolHandler
{
    private readonly string _basePath;
    private readonly ILogger<FileToolHandler> _logger;
    private readonly FileToolOptions _options;

    public FileToolHandler(
        ClickWorkspaceOptions workspaceOptions,
        ILogger<FileToolHandler> logger,
        FileToolOptions options)
    {
        _basePath = workspaceOptions.GetResolvedBasePath();
        _logger = logger;
        _options = options;
    }

    public Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = JsonSerializer.Deserialize<FileArgs>(argumentsJson);
            if (args == null)
                return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error("Ошибка file", "неверные аргументы", "проверь формат JSON")));

            var action = args.Action?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
                return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                    "Ошибка file", "укажите action",
                    "доступные действия: read, list, glob, read_tree, write, append, delete, edit, create_dir, delete_dir, move, copy")));

            var path = string.IsNullOrWhiteSpace(args.Path) && action == "list" ? "." : args.Path;
            if (string.IsNullOrWhiteSpace(path) && action is not "list")
                return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                    "Ошибка file", "укажите path",
                    "используй относительный путь внутри рабочей директории, например: Program.cs или Agents/Common")));

            var safePath = ResolveSafePath(path ?? ".");
            if (safePath == null)
                return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                    "Ошибка file",
                    $"путь '{path}' выходит за пределы рабочей директории или некорректен",
                    "используй относительные пути внутри рабочей директории; абсолютные пути и выход за пределы запрещены")));

            string? safeDest = null;
            if (!string.IsNullOrWhiteSpace(args.DestPath))
            {
                safeDest = ResolveSafePath(args.DestPath);
                if (safeDest == null)
                    return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                        "Ошибка file",
                        $"dest_path '{args.DestPath}' выходит за пределы рабочей директории",
                        "используй относительные пути внутри рабочей директории")));
            }

            return action switch
            {
                "read" => Task.FromResult(WrapFileResult("Read", safePath, () => Read(safePath, args.Offset, args.Limit))),
                "list" => Task.FromResult(WrapFileResult("List", safePath, () => List(safePath))),
                "glob" => Task.FromResult(WrapFileResult("Glob", safePath + (string.IsNullOrEmpty(args.Pattern) ? "" : $" | {args.Pattern}"), () => Glob(safePath, args.Pattern))),
                "read_tree" => Task.FromResult(WrapFileResult("ReadTree", safePath, () => ReadTree(safePath, args.MaxDepth))),
                "write" => Task.FromResult(WrapFileResult("Write", safePath, () => Write(safePath, args.Content ?? ""))),
                "append" => Task.FromResult(WrapFileResult("Append", safePath, () => Append(safePath, args.Content ?? ""))),
                "delete" => Task.FromResult(WrapFileResult("Delete", safePath, () => Delete(safePath))),
                "edit" => Task.FromResult(WrapFileResult("Edit", safePath, () => EditBlock(safePath, args.Content ?? ""))),
                "create_dir" => Task.FromResult(WrapFileResult("CreateDir", safePath, () => CreateDir(safePath))),
                "delete_dir" => Task.FromResult(WrapFileResult("DeleteDir", safePath, () => DeleteDir(safePath, args.Recursive ?? false))),
                "move" => Task.FromResult(safeDest != null ? WrapFileResult("Move", $"{safePath} -> {safeDest}", () => Move(safePath, safeDest)) : ToolResult.FromString(ToolResultFormatter.Error("Ошибка file", "укажите dest_path", "для move требуется dest_path"))),
                "copy" => Task.FromResult(safeDest != null ? WrapFileResult("Copy", $"{safePath} -> {safeDest}", () => Copy(safePath, safeDest)) : ToolResult.FromString(ToolResultFormatter.Error("Ошибка file", "укажите dest_path", "для copy требуется dest_path"))),
                _ => Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                    "Ошибка file", $"неизвестный action '{args.Action}'",
                    "доступные действия: read, list, glob, read_tree, write, append, delete, edit, create_dir, delete_dir, move, copy")))
            };
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                "Ошибка file", "некорректный JSON в arguments: " + ex.Message,
                "проверь формат JSON и имена полей")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                "Ошибка file", ex.Message, "проверь корректность аргументов и пути")));
        }
    }

    private string? ResolveSafePath(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_basePath, path.TrimStart('/', '\\')));
        if (!full.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            return null;
        if (full.Length > _basePath.Length &&
            full[_basePath.Length] != Path.DirectorySeparatorChar &&
            full[_basePath.Length] != Path.AltDirectorySeparatorChar)
            return null;
        return full;
    }

    private ToolResult WrapFileResult(string operation, string path, Func<string> action)
    {
        _logger.LogInformation("[File:{Operation}] {Path}", operation, path);
        var result = action();
        return ToolResult.Structured(new { Operation = operation, Path = path, Result = result }, result);
    }

    private string Glob(string path, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResultFormatter.Error(
                "Ошибка file", "укажи pattern для glob",
                "например: **/*.cs, Agents/**/*.cs, *.json");

        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir))
            return ToolResultFormatter.Error(
                "Ошибка file", $"директория не найдена для glob '{pattern}'",
                "проверь путь или используй list для родительской директории");

        var searchPattern = pattern;
        var relativeRoot = dir;

        // Handle ** patterns: search recursively from base directory
        if (pattern.Contains("**"))
        {
            var parts = pattern.Split(["**"], StringSplitOptions.None);
            if (parts.Length >= 2)
            {
                // If pattern starts with **, search from basePath recursively
                if (pattern.StartsWith("**"))
                {
                    // Walk recursively and filter by the suffix pattern
                    var suffix = parts[^1].TrimStart('/', '\\');
                    relativeRoot = _basePath;
                    var files = new List<string>();
                    CollectGlobFiles(_basePath, _basePath, "**/" + suffix, files, int.MaxValue);
                    if (files.Count == 0)
                        return $"glob '{pattern}' не найдено совпадений";
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    return string.Join("\n", files.Take(100));
                }

                // Pattern like "Agents/**/*.cs" — walk from the first part
                var prefix = parts[0].TrimEnd('/', '\\');
                relativeRoot = Path.Combine(_basePath, prefix);
                if (!Directory.Exists(relativeRoot))
                    return ToolResultFormatter.Error(
                        "Ошибка file", $"директория '{prefix}' не найдена",
                        "проверь путь через list");
                var suffix2 = parts[^1].TrimStart('/', '\\');
                var files2 = new List<string>();
                CollectGlobFiles(relativeRoot, _basePath, "**/" + suffix2, files2, int.MaxValue);
                if (files2.Count == 0)
                    return $"glob '{pattern}' не найдено совпадений";
                files2.Sort(StringComparer.OrdinalIgnoreCase);
                return string.Join("\n", files2.Take(100));
            }
        }

        // Simple glob without **
        var results = new List<string>();
        try
        {
            var matchedFiles = Directory.GetFiles(relativeRoot, searchPattern, SearchOption.TopDirectoryOnly);
            results.AddRange(matchedFiles.Select(f => Path.GetRelativePath(_basePath, f)));
            var matchedDirs = Directory.GetDirectories(relativeRoot, searchPattern, SearchOption.TopDirectoryOnly);
            results.AddRange(matchedDirs.Select(d => Path.GetRelativePath(_basePath, d) + "/"));
        }
        catch (DirectoryNotFoundException)
        {
            return ToolResultFormatter.Error("Ошибка file", $"директория не найдена для glob", "проверь путь");
        }

        if (results.Count == 0)
            return $"glob '{pattern}' не найдено совпадений";
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", results.Take(100));
    }

    private void CollectGlobFiles(string dir, string basePath, string relativePattern, List<string> results, int maxDepth, int currentDepth = 0)
    {
        if (currentDepth >= maxDepth || results.Count >= 200) return;
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var relPath = Path.GetRelativePath(basePath, file);
                var fileName = Path.GetFileName(file);
                if (MatchesSimplePattern(fileName, relativePattern))
                    results.Add(relPath);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (!dirName.StartsWith("."))
                    CollectGlobFiles(subDir, basePath, relativePattern, results, maxDepth, currentDepth + 1);
            }
        }
        catch { }
    }

    private static bool MatchesSimplePattern(string name, string pattern)
    {
        // Extract the file pattern part from "**/pattern"
        var filePattern = pattern;
        var slashIdx = pattern.LastIndexOf('/');
        if (slashIdx >= 0) filePattern = pattern[(slashIdx + 1)..];

        // Simple wildcard matching (* and ?)
        var regex = "^" + Regex.Escape(filePattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    private static string ReadTree(string path, int? maxDepth = null)
    {
        if (!Directory.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file", $"директория '{Path.GetFileName(path)}' не найдена",
                "проверь путь через list");

        var depth = Math.Clamp(maxDepth ?? 3, 1, 10);
        var sb = new StringBuilder();
        BuildTree(path, "", 0, depth, sb);
        return sb.Length > 0 ? sb.ToString() : "(пустая директория)";
    }

    private static void BuildTree(string dir, string indent, int currentDepth, int maxDepth, StringBuilder sb)
    {
        if (currentDepth >= maxDepth) return;
        try
        {
            var entries = Directory.GetFileSystemEntries(dir)
                .Where(e => !Path.GetFileName(e).StartsWith("."))
                .OrderBy(e => Directory.Exists(e) ? 0 : 1)
                .ThenBy(e => Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < entries.Count; i++)
            {
                var isLast = i == entries.Count - 1;
                var name = Path.GetFileName(entries[i]);
                var prefix = isLast ? "└── " : "├── ";
                var childIndent = isLast ? "    " : "│   ";

                if (Directory.Exists(entries[i]))
                {
                    sb.AppendLine($"{indent}{prefix}{name}/");
                    BuildTree(entries[i], indent + childIndent, currentDepth + 1, maxDepth, sb);
                }
                else
                {
                    sb.AppendLine($"{indent}{prefix}{name}");
                }
            }
        }
        catch { }
    }

    private static string List(string path)
    {
        if (!Directory.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file", $"директория '{Path.GetFileName(path)}' не найдена",
                "проверь путь или используй list для родительской директории");
        var items = Directory.GetFileSystemEntries(path)
            .Where(p => !Path.GetFileName(p).StartsWith("."))
            .Select(p => (Path.GetFileName(p) ?? "") + (Directory.Exists(p) ? "/" : ""))
            .OrderBy(x => x);
        return string.Join("\n", items);
    }

    private string Read(string path, int? offset = null, int? limit = null)
    {
        if (!File.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file", $"файл '{Path.GetFileName(path)}' не найден",
                "сначала получи список файлов через list");

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var totalLines = lines.Length;

        if (offset.HasValue || limit.HasValue)
        {
            var start = Math.Max(0, (offset ?? 1) - 1);
            var defaultLimit = _options.DefaultReadLimit > 0 ? _options.DefaultReadLimit : 250;
            var count = Math.Min(limit ?? defaultLimit, lines.Length - start);
            if (start >= lines.Length)
                return $"Запрошенный offset за пределами файла (всего строк: {totalLines})";

            var slice = lines.Skip(start).Take(count).ToList();
            var result = string.Join("\n", slice);
            var prefix = start > 0 ? $"[... {start} строк пропущено ...]\n" : "";
            var nextOffset = start + count + 1;
            var suffix = (start + count) < totalLines ? $"\n[... {totalLines - start - count} строк осталось. Для продолжения: read с offset={nextOffset} limit={count} ...]" : "";
            return $"{prefix}{result}{suffix}";
        }

        var maxChars = _options.MaxReadChars > 0 ? _options.MaxReadChars : 12000;
        var content = File.ReadAllText(path, Encoding.UTF8);
        if (content.Length <= maxChars)
            return content;

        var half = maxChars / 2;
        return content[..half] + $"\n\n[... {content.Length - maxChars} символов пропущено. Используй file read с offset=1 limit=250 для постраничного чтения ...]\n\n" + content[^half..];
    }

    private static string Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, Encoding.UTF8);
        return $"Записано {content.Length} символов в {Path.GetFileName(path)}";
    }

    private static string Append(string path, string content)
    {
        if (!File.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file", $"файл '{Path.GetFileName(path)}' не найден",
                "используй write для создания файла");
        File.AppendAllText(path, content, Encoding.UTF8);
        return $"Добавлено {content.Length} символов в конец {Path.GetFileName(path)}";
    }

    private static string Delete(string path)
    {
        if (!File.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file", $"файл '{Path.GetFileName(path)}' не найден",
                "проверь путь через list");
        File.Delete(path);
        return $"Удалён {Path.GetFileName(path)}";
    }

    private static string EditBlock(string path, string content)
    {
        if (!File.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file", $"файл '{Path.GetFileName(path)}' не найден",
                "сначала прочитай файл через read");
        if (string.IsNullOrEmpty(content))
            return ToolResultFormatter.Error(
                "Ошибка file", "укажи content с SEARCH/REPLACE блоками",
                "используй точный текст из файла");

        if (!content.Contains("<<<<<<< SEARCH"))
        {
            var parts = content.Split(new[] { "\n---\n" }, StringSplitOptions.None);
            if (parts.Length == 2)
                return EditWithSmartSearch(path, parts[0].Trim(), parts[1].Trim());
        }

        var blocks = ParseSearchReplaceBlocks(content);
        if (blocks.Count == 0)
            return ToolResultFormatter.Error(
                "Ошибка file", "неверный формат SEARCH/REPLACE",
                "используй:\n<<<<<<< SEARCH\nстарый код\n=======\nновый код\n>>>>>>> REPLACE\n\nИли упрощенный:\nстарый код\n---\nновый код");

        string lastResult = "";
        foreach (var (search, replace) in blocks)
        {
            lastResult = EditWithSmartSearch(path, search, replace);
            if (!lastResult.StartsWith("✓"))
                return lastResult;
        }
        return lastResult;
    }

    private static List<(string Search, string Replace)> ParseSearchReplaceBlocks(string content)
    {
        var blocks = new List<(string, string)>();
        int pos = 0;
        while (true)
        {
            var searchStart = content.IndexOf("<<<<<<< SEARCH", pos, StringComparison.Ordinal);
            if (searchStart < 0) break;

            var separator = content.IndexOf("\n=======\n", searchStart, StringComparison.Ordinal);
            if (separator < 0) break;

            var replaceEnd = content.IndexOf(">>>>>>> REPLACE", separator, StringComparison.Ordinal);
            if (replaceEnd < 0) break;

            var searchLen = searchStart + "<<<<<<< SEARCH".Length;
            var searchText = content[searchLen..separator].Trim();
            var replaceStart = separator + "\n=======\n".Length;
            var replaceText = content[replaceStart..replaceEnd].Trim();

            blocks.Add((searchText, replaceText));
            pos = replaceEnd + ">>>>>>> REPLACE".Length;
        }
        return blocks;
    }

    private static string EditWithSmartSearch(string path, string searchText, string replaceText)
    {
        var content = File.ReadAllText(path, Encoding.UTF8);
        var fileName = Path.GetFileName(path);

        var idx = content.IndexOf(searchText, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var result = content[..idx] + replaceText + content[(idx + searchText.Length)..];
            File.WriteAllText(path, result, Encoding.UTF8);
            var contextAfter = ShowReplacedContext(result, fileName, idx, replaceText);
            return contextAfter;
        }

        var searchNormalized = NormalizeWhitespace(searchText);
        var contentNormalized = NormalizeWhitespace(content);
        var idxNorm = contentNormalized.IndexOf(searchNormalized, StringComparison.Ordinal);

        if (idxNorm >= 0)
        {
            var result = ReplaceIgnoringWhitespace(content, searchText, replaceText);
            if (result != null)
            {
                File.WriteAllText(path, result, Encoding.UTF8);
                return $"✓ Успешно заменено в {fileName} (с нормализацией пробелов). Перечитай изменённый участок через file read для проверки.";
            }
        }

        var firstLine = searchText.Split('\n')[0].Trim();
        var similarLines = File.ReadAllLines(path, Encoding.UTF8)
            .Select((line, i) => new { Line = line, Index = i })
            .Where(x => x.Line.Contains(firstLine, StringComparison.OrdinalIgnoreCase) ||
                       NormalizeWhitespace(x.Line).Contains(NormalizeWhitespace(firstLine), StringComparison.OrdinalIgnoreCase))
            .Select(x => $"  Строка {x.Index + 1}: {x.Line.Trim()}")
            .Take(5)
            .ToList();

        var errorMsg = $"✗ Текст не найден в {fileName}\n\nИскал:\n{searchText}\n\n";

        if (similarLines.Count > 0)
        {
            errorMsg += "Похожие строки:\n" + string.Join("\n", similarLines);
            errorMsg += "\n\nСовет: скопируй точный текст из файла (с пробелами и отступами) и попробуй снова";
        }

        return errorMsg;
    }

    private static string ShowReplacedContext(string newContent, string fileName, int replacementIndex, string replaceText)
    {
        var lines = newContent.Split('\n');
        var replaceLines = replaceText.Split('\n');

        // Count newlines before replacementIndex to find the line number
        int newlines = 0;
        for (int i = 0; i < replacementIndex && i < newContent.Length; i++)
            if (newContent[i] == '\n') newlines++;
        var lineNumber = newlines + 1;

        // Show ±3 lines around the replaced block
        var contextStart = Math.Max(0, lineNumber - 1 - 3);
        var contextEnd = Math.Min(lines.Length, lineNumber - 1 + replaceLines.Length + 3);
        var contextLines = new List<string>();

        for (int i = contextStart; i < contextEnd; i++)
        {
            var marker = (i >= lineNumber - 1 && i < lineNumber - 1 + replaceLines.Length) ? "→" : " ";
            contextLines.Add($"{i + 1,4}: {marker} {lines[i]}");
        }

        return $"✓ Успешно заменено в {fileName} (строка {lineNumber})\n" +
               $"Заменено {replaceLines.Length} строк. Контекст:\n" +
               string.Join("\n", contextLines);
    }

    private static string NormalizeWhitespace(string text)
    {
        var lines = text.Split('\n');
        return string.Join("\n", lines.Select(l => string.Join(" ", l.Trim().Split(new char[0], StringSplitOptions.RemoveEmptyEntries))));
    }

    private static string? ReplaceIgnoringWhitespace(string content, string search, string replace)
    {
        var searchLines = search.Split('\n');
        var contentLines = content.Split('\n');

        for (int i = 0; i <= contentLines.Length - searchLines.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < searchLines.Length; j++)
            {
                if (NormalizeWhitespace(contentLines[i + j]) != NormalizeWhitespace(searchLines[j]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                var replaceLines = replace.Split('\n');
                var originalIndent = GetIndent(contentLines[i]);

                var newLines = new List<string>();
                newLines.AddRange(contentLines.Take(i));

                foreach (var replLine in replaceLines)
                {
                    newLines.Add(originalIndent + replLine.TrimStart());
                }

                newLines.AddRange(contentLines.Skip(i + searchLines.Length));
                return string.Join("\n", newLines);
            }
        }

        return null;
    }

    private static string GetIndent(string line)
    {
        return new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
    }

    private static string CreateDir(string path)
    {
        if (Directory.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file",
                $"директория '{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' уже существует",
                "используй существующую директорию");
        Directory.CreateDirectory(path);
        return $"Создана папка {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
    }

    private static string DeleteDir(string path, bool recursive)
    {
        if (!Directory.Exists(path))
            return ToolResultFormatter.Error(
                "Ошибка file",
                $"папка '{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' не найдена",
                "проверь путь через list");
        if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
            return ToolResultFormatter.Error(
                "Ошибка file", "папка не пуста",
                "укажи recursive: true для удаления с содержимым");
        Directory.Delete(path, recursive);
        return $"Удалена папка {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
    }

    private static string Move(string source, string dest)
    {
        if (File.Exists(source))
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Move(source, dest);
            return $"Файл перемещён в {Path.GetFileName(dest)}";
        }
        if (Directory.Exists(source))
        {
            if (Directory.Exists(dest) || File.Exists(dest))
                return ToolResultFormatter.Error(
                    "Ошибка file", "путь назначения уже существует",
                    "выбери другое имя или удалите существующий файл/папку");
            Directory.Move(source, dest);
            return $"Папка перемещена в {Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
        }
        return ToolResultFormatter.Error(
            "Ошибка file", $"источник '{Path.GetFileName(source)}' не найден",
            "проверь путь через list");
    }

    private static string Copy(string source, string dest)
    {
        if (File.Exists(source))
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(source, dest, overwrite: true);
            return $"Файл скопирован в {Path.GetFileName(dest)}";
        }
        if (Directory.Exists(source))
        {
            CopyDirectory(source, dest);
            return $"Папка скопирована в {Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
        }
        return ToolResultFormatter.Error(
            "Ошибка file", $"источник '{Path.GetFileName(source)}' не найден",
            "проверь путь через list");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}

public class FileToolOptions
{
    public const string SectionName = "File";

    public int MaxReadChars { get; set; } = 12000;
    public int DefaultReadLimit { get; set; } = 250;
}

public record FileArgs
{
    [JsonPropertyName("action")]
    [ToolParameter(Type = "string", Description = "read | list | glob | read_tree | write | append | delete | edit | create_dir | delete_dir | move | copy", Required = true, Enum = new[] { "read", "list", "glob", "read_tree", "write", "append", "delete", "edit", "create_dir", "delete_dir", "move", "copy" })]
    public string? Action { get; init; }

    [JsonPropertyName("path")]
    [ToolParameter(Type = "string", Description = "Относительный путь к файлу/папке")]
    public string? Path { get; init; }

    [JsonPropertyName("dest_path")]
    [ToolParameter(Type = "string", Description = "Путь назначения для move/copy")]
    public string? DestPath { get; init; }

    [JsonPropertyName("content")]
    [ToolParameter(Type = "string", Description = "Код файла (write/append) или SEARCH/REPLACE блок (edit)")]
    public string? Content { get; init; }

    [JsonPropertyName("offset")]
    [ToolParameter(Type = "number", Description = "Для read: начальная строка (1-based)")]
    public int? Offset { get; init; }

    [JsonPropertyName("limit")]
    [ToolParameter(Type = "number", Description = "Для read: макс. строк (по умолчанию 250)")]
    public int? Limit { get; init; }

    [JsonPropertyName("recursive")]
    [ToolParameter(Type = "boolean", Description = "Для delete_dir: удалить с содержимым")]
    public bool? Recursive { get; init; }

    [JsonPropertyName("pattern")]
    [ToolParameter(Type = "string", Description = "Для glob: маска файлов (напр. **/*.cs, *.json, Agents/**/*.cs)")]
    public string? Pattern { get; init; }

    [JsonPropertyName("max_depth")]
    [ToolParameter(Type = "number", Description = "Для read_tree: максимальная глубина (по умолчанию 3, макс 10)")]
    public int? MaxDepth { get; init; }
}
