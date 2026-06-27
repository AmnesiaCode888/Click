using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSharp;

namespace Click.Agents.Common.Tools;

public class SearchToolHandler : IToolHandler
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public SearchToolHandler(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public async Task<string?> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ToolArgumentValidator.TryValidateJson<SearchArgs>(argumentsJson, out var args, out var err) || args == null)
                return FormatSearchError("неверные arguments для search", err);

            if (string.IsNullOrWhiteSpace(args.Query))
                return FormatSearchError("укажите query", "передай поисковый запрос в поле query");

            if (string.IsNullOrWhiteSpace(_apiKey))
                return FormatSearchError("API ключ Serper не настроен", "добавь Serper:ApiKey в appsettings.json или не используй поиск");

            var maxResults = Math.Clamp(args.MaxResults ?? 5, 1, 10);

            Console.WriteLine($"[Search] {args.Query}");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search");
            request.Headers.Add("X-API-KEY", _apiKey);
            var body = JsonSerializer.Serialize(new { q = args.Query });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);
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
                return FormatSearchError("результаты не найдены", "попробуй переформулировать запрос или используй web_read с известным URL");

            return string.Join("\n", lines);
        }
        catch (HttpRequestException ex)
        {
            var status = ex.StatusCode.HasValue ? $"{(int)ex.StatusCode.Value} " : "";
            return FormatSearchError($"ошибка сети ({status}{ex.Message})", "проверь подключение и API ключ Serper");
        }
        catch (Exception ex)
        {
            return FormatSearchError(ex.Message, "попробуй упростить запрос");
        }
    }

    private static string FormatSearchError(string message, string? hint)
    {
        var sb = new StringBuilder();
        sb.Append($"Ошибка search: {message}");
        if (!string.IsNullOrEmpty(hint))
            sb.Append($". Подсказка: {hint}");
        return sb.ToString();
    }
}

public class SearchArgs
{
    [JsonPropertyName("query")]
    [ToolParameter(Type = "string", Description = "Поисковый запрос", Required = true)]
    public string? Query { get; set; }

    [JsonPropertyName("max_results")]
    [ToolParameter(Type = "number", Description = "Макс. результатов (1–20)")]
    public int? MaxResults { get; set; }
}
