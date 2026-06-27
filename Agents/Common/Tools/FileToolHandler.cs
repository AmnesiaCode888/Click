using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSharp;
using Click;

namespace Click.Agents.Common.Tools;

public class FileToolHandler : IToolHandler
{
    private readonly string _basePath;

    public FileToolHandler(ClickWorkspaceOptions options)
    {
        _basePath = options.GetResolvedBasePath();
    }

    public Task<string?> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = JsonSerializer.Deserialize<FileArgs>(argumentsJson);
            if (args == null)
                return Task.FromResult<string?>("Ошибка: неверные аргументы");

            var action = args.Action?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
                return Task.FromResult<string?>(FormatFileError("укажите action", "доступные действия: read, list, write, append, delete, edit, create_dir, delete_dir, move, copy"));

            var path = string.IsNullOrWhiteSpace(args.Path) && action == "list" ? "." : args.Path;
            if (string.IsNullOrWhiteSpace(path) && action is not "list")
                return Task.FromResult<string?>(FormatFileError("укажите path", "используй относительный путь внутри рабочей директории, например: Program.cs или Agents/Common"));

            var safePath = ResolveSafePath(path ?? ".");
            if (safePath == null)
                return Task.FromResult<string?>(FormatFileError($"путь '{path}' выходит за пределы рабочей директории или некорректен", "используй относительные пути внутри рабочей директории; абсолютные пути и выход за пределы запрещены"));

            string? safeDest = null;
            if (!string.IsNullOrWhiteSpace(args.DestPath))
            {
                safeDest = ResolveSafePath(args.DestPath);
                if (safeDest == null)
                    return Task.FromResult<string?>(FormatFileError($"dest_path '{args.DestPath}' выходит за пределы рабочей директории", "используй относительные пути внутри рабочей директории"));
            }

            return action switch
            {
                "read" => Task.FromResult<string?>(LogAndExecute("Read", safePath, () => Read(safePath, args.Offset, args.Limit))),
                "list" => Task.FromResult<string?>(LogAndExecute("List", safePath, () => List(safePath))),
                "write" => Task.FromResult<string?>(LogAndExecute("Write", safePath, () => Write(safePath, args.Content ?? ""))),
                "append" => Task.FromResult<string?>(LogAndExecute("Append", safePath, () => Append(safePath, args.Content ?? ""))),
                "delete" => Task.FromResult<string?>(LogAndExecute("Delete", safePath, () => Delete(safePath))),
                "edit" => Task.FromResult<string?>(LogAndExecute("Edit", safePath, () => EditBlock(safePath, args.Content ?? ""))),
                "create_dir" => Task.FromResult<string?>(LogAndExecute("CreateDir", safePath, () => CreateDir(safePath))),
                "delete_dir" => Task.FromResult<string?>(LogAndExecute("DeleteDir", safePath, () => DeleteDir(safePath, args.Recursive ?? false))),
                "move" => Task.FromResult<string?>(safeDest != null ? LogAndExecute("Move", $"{safePath} -> {safeDest}", () => Move(safePath, safeDest)) : FormatFileError("укажите dest_path", "для move требуется dest_path")),
                "copy" => Task.FromResult<string?>(safeDest != null ? LogAndExecute("Copy", $"{safePath} -> {safeDest}", () => Copy(safePath, safeDest)) : FormatFileError("укажите dest_path", "для copy требуется dest_path")),
                _ => Task.FromResult<string?>(FormatFileError($"неизвестный action '{args.Action}'", "доступные действия: read, list, write, append, delete, edit, create_dir, delete_dir, move, copy"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>(FormatFileError(ex.Message, "проверь корректность аргументов и пути"));
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

    private string LogAndExecute(string operation, string path, Func<string> action)
    {
        Console.WriteLine($"[File:{operation}] {path}");
        return action();
    }

    private static string FormatFileError(string message, string? hint)
    {
        var sb = new StringBuilder();
        sb.Append($"Ошибка file: {message}");
        if (!string.IsNullOrEmpty(hint))
            sb.Append($". Подсказка: {hint}");
        return sb.ToString();
    }

    private static string List(string path)
    {
        if (!Directory.Exists(path))
            return FormatFileError($"директория '{Path.GetFileName(path)}' не найдена", "проверь путь или используй list для родительской директории");
        var items = Directory.GetFileSystemEntries(path)
            .Where(p => !Path.GetFileName(p).StartsWith("."))
            .Select(p => (Path.GetFileName(p) ?? "") + (Directory.Exists(p) ? "/" : ""))
            .OrderBy(x => x);
        return string.Join("\n", items);
    }

    private static string Read(string path, int? offset = null, int? limit = null)
    {
        if (!File.Exists(path))
            return FormatFileError($"файл '{Path.GetFileName(path)}' не найден", "сначала получи список файлов через list");

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var totalLines = lines.Length;

        if (offset.HasValue || limit.HasValue)
        {
            var start = Math.Max(0, (offset ?? 1) - 1);
            var count = Math.Min(limit ?? 250, lines.Length - start);
            if (start >= lines.Length)
                return $"Запрошенный offset за пределами файла (всего строк: {totalLines})";

            var slice = lines.Skip(start).Take(count).ToList();
            var result = string.Join("\n", slice);
            var prefix = start > 0 ? $"[... {start} строк пропущено ...]\n" : "";
            var suffix = (start + count) < totalLines ? $"\n[... {totalLines - start - count} строк осталось ...]" : "";
            return $"{prefix}{result}{suffix}";
        }

        const int maxChars = 12000;
        var content = File.ReadAllText(path, Encoding.UTF8);
        if (content.Length <= maxChars)
            return content;

        var half = maxChars / 2;
        return content[..half] + $"\n\n[... {content.Length - maxChars} символов пропущено (читай с offset/limit) ...]\n\n" + content[^half..];
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
            return FormatFileError($"файл '{Path.GetFileName(path)}' не найден", "используй write для создания файла");
        File.AppendAllText(path, content, Encoding.UTF8);
        return $"Добавлено {content.Length} символов в конец {Path.GetFileName(path)}";
    }

    private static string Delete(string path)
    {
        if (!File.Exists(path))
            return FormatFileError($"файл '{Path.GetFileName(path)}' не найден", "проверь путь через list");
        File.Delete(path);
        return $"Удалён {Path.GetFileName(path)}";
    }

    private static string EditBlock(string path, string content)
    {
        if (!File.Exists(path))
            return FormatFileError($"файл '{Path.GetFileName(path)}' не найден", "сначала прочитай файл через read");
        if (string.IsNullOrEmpty(content))
            return FormatFileError("укажи content с SEARCH/REPLACE блоками", "используй точный текст из файла");

        if (!content.Contains("<<<<<<< SEARCH"))
        {
            var parts = content.Split(new[] { "\n---\n" }, StringSplitOptions.None);
            if (parts.Length == 2)
                return EditWithSmartSearch(path, parts[0].Trim(), parts[1].Trim());
        }

        var blocks = ParseSearchReplaceBlocks(content);
        if (blocks.Count == 0)
            return FormatFileError("неверный формат SEARCH/REPLACE", "используй:\n<<<<<<< SEARCH\nстарый код\n=======\nновый код\n>>>>>>> REPLACE\n\nИли упрощенный:\nстарый код\n---\nновый код");

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
            return $"✓ Успешно заменено в {fileName}";
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
                return $"✓ Успешно заменено в {fileName} (с нормализацией пробелов)";
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
            return FormatFileError($"директория '{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' уже существует", "используй существующую директорию");
        Directory.CreateDirectory(path);
        return $"Создана папка {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
    }

    private static string DeleteDir(string path, bool recursive)
    {
        if (!Directory.Exists(path))
            return FormatFileError($"папка '{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' не найдена", "проверь путь через list");
        if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
            return FormatFileError("папка не пуста", "укажи recursive: true для удаления с содержимым");
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
                return FormatFileError("путь назначения уже существует", "выбери другое имя или удалите существующий файл/папку");
            Directory.Move(source, dest);
            return $"Папка перемещена в {Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
        }
        return FormatFileError($"источник '{Path.GetFileName(source)}' не найден", "проверь путь через list");
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
        return FormatFileError($"источник '{Path.GetFileName(source)}' не найден", "проверь путь через list");
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

public class FileArgs
{
    [JsonPropertyName("action")]
    [ToolParameter(Type = "string", Description = "read | list | write | append | delete | edit | create_dir | delete_dir | move | copy", Required = true, Enum = new[] { "read", "list", "write", "append", "delete", "edit", "create_dir", "delete_dir", "move", "copy" })]
    public string? Action { get; set; }

    [JsonPropertyName("path")]
    [ToolParameter(Type = "string", Description = "Относительный путь к файлу/папке")]
    public string? Path { get; set; }

    [JsonPropertyName("dest_path")]
    [ToolParameter(Type = "string", Description = "Путь назначения для move/copy")]
    public string? DestPath { get; set; }

    [JsonPropertyName("content")]
    [ToolParameter(Type = "string", Description = "Код файла (write/append) или SEARCH/REPLACE блок (edit)")]
    public string? Content { get; set; }

    [JsonPropertyName("offset")]
    [ToolParameter(Type = "number", Description = "Для read: начальная строка (1-based)")]
    public int? Offset { get; set; }

    [JsonPropertyName("limit")]
    [ToolParameter(Type = "number", Description = "Для read: макс. строк (по умолчанию 250)")]
    public int? Limit { get; set; }

    [JsonPropertyName("recursive")]
    [ToolParameter(Type = "boolean", Description = "Для delete_dir: удалить с содержимым")]
    public bool? Recursive { get; set; }
}
