using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSharp;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class SearchToolHandler : IToolHandler
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<SearchToolHandler> _logger;
    private readonly SearchToolOptions _options;

    public SearchToolHandler(
        HttpClient httpClient,
        string apiKey,
        ILogger<SearchToolHandler> logger,
        SearchToolOptions options)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _logger = logger;
        _options = options;
    }

    public async Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ToolArgumentValidator.TryValidateJson<SearchArgs>(argumentsJson, out var args, out var err) || args == null)
                return ToolResult.FromString(ToolResultFormatter.Error("Ошибка search", "неверные arguments для search", err));

            if (string.IsNullOrWhiteSpace(args.Query))
                return ToolResult.FromString(ToolResultFormatter.Error("Ошибка search", "укажите query", "передай поисковый запрос в поле query"));

            if (string.IsNullOrWhiteSpace(_apiKey))
                return ToolResult.FromString(ToolResultFormatter.Error("Ошибка search", "API ключ Serper не настроен", "добавь Serper:ApiKey в appsettings.json или не используй поиск"));

            var maxResults = Math.Clamp(
                args.MaxResults ?? _options.DefaultMaxResults,
                _options.MinMaxResults,
                _options.MaxMaxResults);

            _logger.LogInformation("[Search] {Query}", args.Query);

            const int maxAttempts = 3;
            Exception? lastEx = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await ExecuteSingleSearchAsync(args.Query, maxResults, cancellationToken);
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    lastEx = ex;
                    var jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
                    var delayMs = 2000 * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay((int)(delayMs * jitter), cancellationToken);
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    lastEx = ex;
                    var jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
                    var delayMs = 1000 * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay((int)(delayMs * jitter), cancellationToken);
                }
            }

            var finalMsg = lastEx is HttpRequestException hre && hre.StatusCode.HasValue
                ? $"ошибка сети ({(int)hre.StatusCode.Value} {hre.Message})"
                : lastEx?.Message ?? "неизвестная ошибка";
            return ToolResult.FromString(ToolResultFormatter.Error(
                "Ошибка search", finalMsg,
                "проверь подключение и API ключ Serper"));
        }
        catch (Exception ex)
        {
            return ToolResult.FromString(ToolResultFormatter.Error("Ошибка search", ex.Message, "попробуй упростить запрос"));
        }
    }

    private async Task<ToolResult> ExecuteSingleSearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search");
        request.Headers.Add("X-API-KEY", _apiKey);
        var body = JsonSerializer.Serialize(new { q = query });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var lines = new List<string>();

        if (root.TryGetProperty("knowledgeGraph", out var kg))
        {
            var title = kg.TryGetProperty("title", out var t) ? t.GetString() : null;
            var desc = kg.TryGetProperty("description", out var d) ? d.GetString() : null;
            if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(desc))
                lines.Add($"📌 {title ?? ""}\n{desc ?? ""}\n");
        }

        if (root.TryGetProperty("organic", out var organic))
        {
            var count = 0;
            foreach (var item in organic.EnumerateArray())
            {
                if (count >= maxResults) break;

                var title = item.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                var link = item.TryGetProperty("link", out var ll) ? ll.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("snippet", out var ss) ? ss.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(link))
                {
                    lines.Add($"📄 {title}\n🔗 {link}\n📝 {snippet}\n");
                    count++;
                }
            }
        }

        if (lines.Count == 0)
            return ToolResult.FromString(ToolResultFormatter.Error(
                "Ошибка search", "результаты не найдены",
                "попробуй переформулировать запрос или используй web_read с известным URL"));

        var formatted = string.Join("\n", lines);
        return ToolResult.Structured(new { Query = query, Results = lines }, formatted);
    }
}

public class SearchToolOptions
{
    public const string SectionName = "Search";

    public int DefaultMaxResults { get; set; } = 5;
    public int MinMaxResults { get; set; } = 1;
    public int MaxMaxResults { get; set; } = 20;
}

public record SearchArgs
{
    [JsonPropertyName("query")]
    [ToolParameter(Type = "string", Description = "Поисковый запрос", Required = true)]
    public string? Query { get; init; }

    [JsonPropertyName("max_results")]
    [ToolParameter(Type = "number", Description = "Макс. результатов (1–20)")]
    public int? MaxResults { get; init; }
}
