using System.Text.Json.Serialization;
using AgentSharp;
using Click.Services.Vector;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class SemanticSearchToolHandler : IToolHandler
{
    private readonly VectorIndexService _indexService;
    private readonly ILogger<SemanticSearchToolHandler> _logger;
    private readonly string _workspacePath;

    public SemanticSearchToolHandler(VectorIndexService indexService, ILogger<SemanticSearchToolHandler> logger, string workspacePath)
    {
        _indexService = indexService;
        _logger = logger;
        _workspacePath = workspacePath;
    }

    public string Name => "semantic_search";

    public string Description => "Семантический поиск по коду проекта. Используй, когда пользователь спрашивает 'где реализовано X', 'найди код для Y', 'покажи функцию которая делает Z'. Работает для любого языка. Возвращает фрагменты кода с путями и строками.";

    public Type ArgsType => typeof(SemanticSearchArgs);

    public async Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<SemanticSearchArgs>(argumentsJson);
            if (args == null || string.IsNullOrWhiteSpace(args.Query))
                return ToolResult.FromString("Ошибка: укажи query — что искать по смыслу.");

            _logger.LogInformation("[SemanticSearch] {Query}", args.Query);

            if (!_indexService.IsAvailable)
            {
                _logger.LogInformation("[SemanticSearch] Embedding not available — falling back to text search");
                return await TextFallbackSearchAsync(args);
            }

            var limit = Math.Clamp(args.Limit ?? 5, 1, 20);
            var results = await _indexService.SearchAsync(args.Query, limit, args.Language, args.Glob, cancellationToken);

            if (results.Count == 0)
                return ToolResult.FromString("Ничего не найдено по запросу. Попробуй переформулировать или проверь, что проект проиндексирован (/index-status).");

            var lines = new List<string>();
            lines.Add($"Результаты семантического поиска ({results.Count} найдено):\n");

            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                lines.Add($"[{i + 1}] Файл: {r.FilePath}, строки {r.StartLine}-{r.EndLine}");
                if (!string.IsNullOrEmpty(r.SymbolType))
                    lines.Add($"    Тип: {r.SymbolType}, Имя: {r.SymbolName}");
                lines.Add("---");
                lines.Add(r.Content.Length > 800 ? r.Content[..800] + "\n... (обрезано)" : r.Content);
                lines.Add("---\n");
            }

            return ToolResult.FromString(string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic search error");
            return ToolResult.FromString($"Ошибка семантического поиска: {ex.Message}");
        }
    }

    private async Task<ToolResult> TextFallbackSearchAsync(SemanticSearchArgs args)
    {
        var query = args.Query ?? "";
        var limit = Math.Clamp(args.Limit ?? 5, 1, 20);
        var terms = query.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}'])
            .Where(w => w.Length >= 2)
            .Distinct()
            .ToArray();

        if (terms.Length == 0)
            return ToolResult.FromString("Семантический поиск недоступен: эмбеддинг не настроен. Укажи Embedding:BaseUrl, Embedding:ApiKey и Embedding:Model в настройках.\n\nFallback: текстовый поиск не дал результатов из-за слишком короткого запроса.");

        var results = new List<(string FilePath, int Line, string Content, int Score)>();
        var codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".cs", ".js", ".ts", ".py", ".go", ".rs", ".java", ".c", ".cpp", ".php", ".rb",
            ".md", ".json", ".yaml", ".xml", ".txt"
        };
        var ignoreDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".git", "node_modules", "bin", "obj", ".click", "dist", "build", "target"
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(_workspacePath, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!codeExts.Contains(ext)) continue;

                var relDir = Path.GetRelativePath(_workspacePath, Path.GetDirectoryName(file) ?? "").Replace('\\', '/');
                if (relDir.Split('/').Any(seg => ignoreDirs.Contains(seg))) continue;

                try
                {
                    var content = await File.ReadAllTextAsync(file);
                    var lowerContent = content.ToLowerInvariant();
                    var score = terms.Count(term => lowerContent.Contains(term));

                    if (score > 0)
                    {
                        var lines = content.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var lineLower = lines[i].ToLowerInvariant();
                            var lineScore = terms.Count(t => lineLower.Contains(t));
                            if (lineScore > 0)
                            {
                                var start = Math.Max(0, i - 2);
                                var end = Math.Min(lines.Length - 1, i + 2);
                                var snippet = string.Join("\n", lines.Skip(start).Take(end - start + 1));
                                var relPath = Path.GetRelativePath(_workspacePath, file).Replace('\\', '/');
                                results.Add((relPath, i + 1, snippet, score + lineScore));
                                if (results.Count >= limit * 3)
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipped file in text fallback: {File}", file);
                }

                if (results.Count >= limit * 3)
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text fallback search scan failed");
        }

        if (results.Count == 0)
            return ToolResult.FromString("Семантический поиск недоступен: эмбеддинг не настроен. Используйте текстовый поиск — настройте Embedding:BaseUrl, Embedding:ApiKey и Embedding:Model.\n\nFallback: текстовый поиск не дал результатов по запросу.");

        var top = results
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();

        var resultLines = new List<string>();
        resultLines.Add($"Результаты текстового поиска (эмбеддинг не настроен, {top.Count} найдено):");
        resultLines.Add($"Подсказка: настройте Embedding для семантического поиска (Embedding:BaseUrl, Embedding:ApiKey, Embedding:Model).\n");

        for (int i = 0; i < top.Count; i++)
        {
            var r = top[i];
            resultLines.Add($"[{i + 1}] Файл: {r.FilePath}, строка {r.Line}");
            resultLines.Add("---");
            resultLines.Add(r.Content.Length > 800 ? r.Content[..800] + "\n... (обрезано)" : r.Content);
            resultLines.Add("---\n");
        }

        return ToolResult.FromString(string.Join("\n", resultLines));
    }
}

public record SemanticSearchArgs
{
    [JsonPropertyName("query")]
    [ToolParameter(Type = "string", Description = "Описание того, что ищем по смыслу (например: 'аутентификация пользователя', 'отправка email')", Required = true)]
    public string? Query { get; init; }

    [JsonPropertyName("limit")]
    [ToolParameter(Type = "number", Description = "Максимум результатов (1-20, по умолчанию 5)")]
    public int? Limit { get; init; }

    [JsonPropertyName("language")]
    [ToolParameter(Type = "string", Description = "Фильтр по языку: c_sharp, javascript, typescript, python, go, rust, java, c, cpp, php, ruby, markdown, json, yaml, unknown")]
    public string? Language { get; init; }

    [JsonPropertyName("glob")]
    [ToolParameter(Type = "string", Description = "Фильтр по пути (SQLite glob): например **/Services/*.cs")]
    public string? Glob { get; init; }
}
