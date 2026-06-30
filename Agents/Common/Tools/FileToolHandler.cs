using System.Text.Json.Serialization;
using AgentSharp;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class FileToolHandler : IToolHandler
{
    private static readonly HashSet<string> ReadOnlyActions =
        new(StringComparer.OrdinalIgnoreCase) { "read", "list", "glob", "read_tree" };

    private readonly string _workspacePath;
    private readonly FileToolOptions _options;
    private readonly ILogger<FileToolHandler> _logger;
    private readonly bool _allowWrite;

    public FileToolHandler(string workspacePath, FileToolOptions options, ILogger<FileToolHandler> logger, bool allowWrite = true)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _options = options;
        _logger = logger;
        _allowWrite = allowWrite;
    }

    public string Name => "file";

    public string Description => _allowWrite
        ? "Работа с файлами проекта (read/write/append/delete/edit/create_dir/move/copy)"
        : "Только чтение файлов проекта (read/list/glob/read_tree)";

    public Type ArgsType => typeof(FileArgs);

    public Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<FileArgs>(argumentsJson)
                ?? throw new ArgumentException("Не удалось десериализовать аргументы");

            if (string.IsNullOrEmpty(args.Action) && !_allowWrite)
                args = args with { Action = "read" };
            if (!_allowWrite && !ReadOnlyActions.Contains(args.Action ?? ""))
                return Task.FromResult(ToolResult.FromString("Ошибка: этот агент работает только на чтение. Запрещённые действия: write, append, delete, edit, create_dir, delete_dir, move, copy."));

            return Task.FromResult(args.Action switch
            {
                "read" => HandleRead(args),
                "write" => HandleWrite(args),
                "append" => HandleAppend(args),
                "delete" => HandleDelete(args),
                "edit" => HandleEdit(args),
                "create_dir" => HandleCreateDir(args),
                "delete_dir" => HandleDeleteDir(args),
                "move" => HandleMove(args),
                "copy" => HandleCopy(args),
                "list" => HandleList(args),
                "glob" => HandleGlob(args),
                "read_tree" => HandleReadTree(args),
                _ => ToolResult.FromString($"Неизвестное действие: {args.Action}")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File tool error");
            return Task.FromResult(ToolResult.FromString($"Ошибка: {ex.Message}"));
        }
    }

    private string ResolvePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return _workspacePath;

        var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, relativePath));

        // Нормализуем workspace path с trailing separator для корректного сравнения
        // Это предотвращает path traversal: D:\clicker не пройдёт проверку для workspace D:\click
        var normalizedWorkspace = _workspacePath.EndsWith(Path.DirectorySeparatorChar)
            ? _workspacePath
            : _workspacePath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, _workspacePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Путь '{relativePath}' выходит за пределы рабочей директории '{_workspacePath}'");
        }

        return fullPath;
    }

    private ToolResult HandleRead(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        if (!File.Exists(path))
            return ToolResult.FromString($"Файл '{args.Path}' не найден");

        var lines = File.ReadAllLines(path);
        var offset = Math.Max(1, args.Offset ?? 1);
        var limit = args.Limit ?? _options.DefaultReadLimit;

        if (offset > lines.Length)
            return ToolResult.FromString($"Файл '{args.Path}' содержит {lines.Length} строк. Указанный offset {offset} выходит за пределы.");

        var startIndex = offset - 1;
        var selectedLines = lines.Skip(startIndex).Take(limit).ToArray();
        var content = string.Join("\n", selectedLines);

        if (content.Length > _options.MaxReadChars)
            content = content[.._options.MaxReadChars] + "\n... (обрезано)";

        if (lines.Length > offset + limit - 1)
            content += $"\n\n... осталось {lines.Length - (offset + limit - 1)} строк (всего {lines.Length}, показано {limit} начиная со строки {offset})";

        return ToolResult.FromString(content);
    }

    private ToolResult HandleWrite(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, args.Content ?? "");
        return ToolResult.FromString($"Записано: {args.Path}");
    }

    private ToolResult HandleAppend(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        File.AppendAllText(path, args.Content ?? "");
        return ToolResult.FromString($"Дописано: {args.Path}");
    }

    private ToolResult HandleDelete(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        if (!File.Exists(path) && !Directory.Exists(path))
            return ToolResult.FromString($"Не найдено: {args.Path}");

        if (File.Exists(path))
            File.Delete(path);
        else
            Directory.Delete(path, recursive: true);

        return ToolResult.FromString($"Удалено: {args.Path}");
    }

    private ToolResult HandleEdit(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        if (!File.Exists(path))
            return ToolResult.FromString($"Файл '{args.Path}' не найден");

        var fileContent = File.ReadAllText(path);
        var contentArg = args.Content ?? "";

        var searchMarker = "<<<<<<< SEARCH";
        var dividerMarker = "=======";
        var replaceMarker = ">>>>>>> REPLACE";

        var searchIdx = contentArg.IndexOf(searchMarker, StringComparison.Ordinal);
        var dividerIdx = contentArg.IndexOf(dividerMarker, StringComparison.Ordinal);
        var replaceIdx = contentArg.IndexOf(replaceMarker, StringComparison.Ordinal);

        if (searchIdx < 0 || dividerIdx < 0 || replaceIdx < 0)
            return ToolResult.FromString("Ошибка: контент должен содержать блоки <<<<<<< SEARCH, =======, >>>>>>> REPLACE");

        var searchStart = searchIdx + searchMarker.Length;
        var search = contentArg[searchStart..dividerIdx].TrimStart('\n', '\r');
        if (search.EndsWith('\n') || search.EndsWith('\r'))
            search = search.TrimEnd('\n', '\r');

        var replace = contentArg[(dividerIdx + dividerMarker.Length)..replaceIdx].TrimStart('\n', '\r');
        if (replace.EndsWith('\n') || replace.EndsWith('\r'))
            replace = replace.TrimEnd('\n', '\r');

        // Normalize line endings for matching only (don't modify the original file's line endings on write)
        var normalizedSearch = search.Replace("\r\n", "\n").Replace("\r", "\n");
        var normalizedFile = fileContent.Replace("\r\n", "\n").Replace("\r", "\n");

        int searchIdxInFile;

        // Try exact match on normalized content
        searchIdxInFile = normalizedFile.IndexOf(normalizedSearch, StringComparison.Ordinal);
        if (searchIdxInFile < 0)
        {
            // Try fuzzy match: trim whitespace from each line, find approximate region
            var searchLines = normalizedSearch.Split('\n');
            var fileLines = normalizedFile.Split('\n');
            var matchLine = FindFuzzyMatch(fileLines, searchLines);
            if (matchLine >= 0)
            {
                // Compute byte offset to the fuzzy-matched line
                searchIdxInFile = 0;
                for (int i = 0; i < matchLine; i++)
                    searchIdxInFile += fileLines[i].Length + 1;

                // Now find the exact position of normalizedSearch within a window
                var windowStart = Math.Max(0, searchIdxInFile - fileLines[matchLine].Length);
                var windowEnd = Math.Min(normalizedFile.Length + 1, searchIdxInFile + normalizedSearch.Length + fileLines[matchLine].Length * 3);
                var exactInWindow = normalizedFile.IndexOf(normalizedSearch, windowStart, windowEnd - windowStart, StringComparison.Ordinal);
                searchIdxInFile = exactInWindow >= 0 ? exactInWindow : -1;
            }
        }

        if (searchIdxInFile < 0)
        {
            var searchFirstLine = search.TrimStart().Split('\n')[0].Trim();
            var hint = $"Точный текст не найден. Сначала вызови file read \"{args.Path}\", скопируй нужный фрагмент и вставь в SEARCH-блок.";
            if (searchFirstLine.Length > 0 && searchFirstLine.Length < 80)
                hint += $"\nИскали: \"{searchFirstLine}\"";
            return ToolResult.FromString($"Ошибка: SEARCH-блок не найден в файле.\n{hint}");
        }

        // Replace in original fileContent (not normalized) to preserve line endings.
        // Map the normalized offset back to the original file.
        var normToOrigOffset = MapNormalizedOffsetToOriginal(fileContent, searchIdxInFile);
        var normToOrigEnd = MapNormalizedOffsetToOriginal(fileContent, searchIdxInFile + normalizedSearch.Length);
        var newContent = fileContent[..normToOrigOffset] + replace + fileContent[normToOrigEnd..];
        File.WriteAllText(path, newContent);
        return ToolResult.FromString($"Изменено: {args.Path}");
    }

    /// <summary>
    /// Maps a character offset in the normalized text (all \n) back to the original text
    /// (which may contain \r\n). This preserves the original line endings on write.
    /// </summary>
    private static int MapNormalizedOffsetToOriginal(string original, int normOffset)
    {
        int origPos = 0;
        int normPos = 0;
        while (normPos < normOffset && origPos < original.Length)
        {
            if (original[origPos] == '\r')
            {
                // Skip \r — it's absent from normalized text
            }
            else
            {
                normPos++;
            }
            origPos++;
        }
        return origPos;
    }

    private static int FindFuzzyMatch(string[] fileLines, string[] searchLines)
    {
        if (searchLines.Length == 0 || fileLines.Length < searchLines.Length)
            return -1;

        for (int i = 0; i <= fileLines.Length - searchLines.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < searchLines.Length; j++)
            {
                var fileLine = fileLines[i + j].Trim();
                var searchLine = searchLines[j].Trim();
                if (string.IsNullOrEmpty(fileLine) && string.IsNullOrEmpty(searchLine))
                    continue;
                if (fileLine != searchLine)
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private ToolResult HandleCreateDir(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        Directory.CreateDirectory(path);
        return ToolResult.FromString($"Создана директория: {args.Path}");
    }

    private ToolResult HandleDeleteDir(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        if (!Directory.Exists(path))
            return ToolResult.FromString($"Директория '{args.Path}' не найдена");

        Directory.Delete(path, recursive: args.Recursive ?? false);
        return ToolResult.FromString($"Удалена директория: {args.Path}");
    }

    private ToolResult HandleMove(FileArgs args)
    {
        var source = ResolvePath(args.Path);
        var dest = ResolvePath(args.DestPath);

        if (!File.Exists(source) && !Directory.Exists(source))
            return ToolResult.FromString($"Источник '{args.Path}' не найден");

        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(source))
            File.Move(source, dest, overwrite: true);
        else
            Directory.Move(source, dest);

        return ToolResult.FromString($"Перемещено: {args.Path} -> {args.DestPath}");
    }

    private ToolResult HandleCopy(FileArgs args)
    {
        var source = ResolvePath(args.Path);
        var dest = ResolvePath(args.DestPath);

        if (!File.Exists(source) && !Directory.Exists(source))
            return ToolResult.FromString($"Источник '{args.Path}' не найден");

        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(source))
            File.Copy(source, dest, overwrite: true);
        else
            CopyDirectory(source, dest);

        return ToolResult.FromString($"Скопировано: {args.Path} -> {args.DestPath}");
    }

    private ToolResult HandleList(FileArgs args)
    {
        var path = ResolvePath(args.Path);
        if (!Directory.Exists(path))
            return ToolResult.FromString($"Директория '{args.Path}' не найдена");

        var entries = Directory.GetFileSystemEntries(path);
        var result = new List<string>();
        foreach (var e in entries)
        {
            try
            {
                var attr = File.GetAttributes(e);
                var isDir = attr.HasFlag(FileAttributes.Directory);
                result.Add(isDir ? Path.GetFileName(e) + "/" : Path.GetFileName(e));
            }
            catch (IOException)
            {
                result.Add(Path.GetFileName(e) + " <-- ошибка доступа");
            }
        }

        return ToolResult.FromString(string.Join("\n", result));
    }

    private ToolResult HandleGlob(FileArgs args)
    {
        var pattern = args.Pattern ?? "*";
        var basePath = ResolvePath(args.Path ?? ".");

        if (!Directory.Exists(basePath))
            return ToolResult.FromString($"Директория '{args.Path}' не найдена");

        // Simple glob implementation
        if (pattern.Contains("**"))
        {
            var results = new List<string>();
            var parts = pattern.Split("**", 2);
            var prefix = parts[0].TrimEnd('/', '\\');
            var suffix = parts.Length > 1 ? parts[1].TrimStart('/', '\\') : "";

            var searchRoot = string.IsNullOrEmpty(prefix) ? basePath : Path.Combine(basePath, prefix);
            if (!Directory.Exists(searchRoot))
                return ToolResult.FromString("");

            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(basePath, file).Replace('\\', '/');
                if (string.IsNullOrEmpty(suffix) || MatchesGlob(relativePath, suffix))
                    results.Add(relativePath);
            }

            return ToolResult.FromString(string.Join("\n", results.Take(100)));
        }
        else
        {
            var results = Directory.EnumerateFiles(basePath, pattern, SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetRelativePath(basePath, f).Replace('\\', '/'));
            return ToolResult.FromString(string.Join("\n", results.Take(100)));
        }
    }

    private static bool MatchesGlob(string path, string pattern)
    {
        // Simple wildcard matching
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, regexPattern);
    }

    private ToolResult HandleReadTree(FileArgs args)
    {
        var path = ResolvePath(args.Path ?? ".");
        if (!Directory.Exists(path))
            return ToolResult.FromString($"Директория '{args.Path}' не найдена");

        var maxDepth = Math.Clamp(args.MaxDepth ?? 3, 1, 10);
        var result = new List<string>();
        ReadTreeRecursive(path, "", 0, maxDepth, result);
        return ToolResult.FromString(string.Join("\n", result));
    }

    private void ReadTreeRecursive(string dir, string indent, int depth, int maxDepth, List<string> result)
    {
        if (depth >= maxDepth) return;

        try
        {
            var entries = Directory.GetFileSystemEntries(dir)
                .OrderBy(e => IsFileSafe(e))
                .ThenBy(e =>
                {
                    try { return Path.GetFileName(e); }
                    catch { return ""; }
                });

            foreach (var entry in entries)
            {
                try
                {
                    var isFile = IsFileSafe(entry);
                    var name = Path.GetFileName(entry);

                    if (isFile)
                    {
                        result.Add($"{indent}{name}");
                    }
                    else
                    {
                        result.Add($"{indent}{name}/");
                        ReadTreeRecursive(entry, indent + "  ", depth + 1, maxDepth, result);
                    }
                }
                catch (IOException)
                {
                    result.Add($"{indent}{Path.GetFileName(entry)} <-- ошибка доступа");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            result.Add($"{indent}[access denied]");
        }
        catch (DirectoryNotFoundException)
        {
            result.Add($"{indent}[directory not found]");
        }
    }

    private static bool IsFileSafe(string path)
    {
        try
        {
            return !File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch
        {
            return true;
        }
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
