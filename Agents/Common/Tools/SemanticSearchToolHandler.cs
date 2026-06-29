using System.Text.Json.Serialization;
using AgentSharp;
using Click.Services.Vector;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class SemanticSearchToolHandler : IToolHandler
{
    private readonly VectorIndexService _indexService;
    private readonly ILogger<SemanticSearchToolHandler> _logger;

    public SemanticSearchToolHandler(VectorIndexService indexService, ILogger<SemanticSearchToolHandler> logger)
    {
        _indexService = indexService;
        _logger = logger;
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
                return ToolResult.FromString("Семантический поиск недоступен: эмбеддинг не настроен. Укажи Embedding:BaseUrl, Embedding:ApiKey и Embedding:Model в настройках.");

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
