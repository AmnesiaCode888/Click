using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Text;
using AgentSharp;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class WebReadToolHandler : IToolHandler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebReadToolHandler> _logger;
    private readonly WebReadToolOptions _options;
    private static readonly Regex UrlRegex = new(@"https?://[^\s\)\)""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public WebReadToolHandler(
        HttpClient httpClient,
        ILogger<WebReadToolHandler> logger,
        WebReadToolOptions options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<string?> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ToolArgumentValidator.TryValidateJson<WebReadArgs>(argumentsJson, out var args, out var err) || args == null)
                return ToolResultFormatter.Error("Ошибка web_read", "неверные arguments для web_read", err);

            if (string.IsNullOrWhiteSpace(args.Url))
                return ToolResultFormatter.Error(
                    "Ошибка web_read", "укажите url",
                    "передай абсолютный URL, например https://example.com/docs");

            var url = ExtractValidUrl(args.Url.Trim());
            if (string.IsNullOrEmpty(url))
                return ToolResultFormatter.Error(
                    "Ошибка web_read", $"некорректный URL '{args.Url}'",
                    "URL должен начинаться с http:// или https://");

            _logger.LogInformation("[Read] {Url}", url);

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//noscript") ?? Enumerable.Empty<HtmlNode>())
                node.Remove();

            var text = doc.DocumentNode.InnerText;
            text = string.Join(" ", text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));

            var maxLen = args.MaxLength ?? _options.DefaultMaxLength;
            if (text.Length > maxLen)
                text = text.Substring(0, maxLen) + "\n... (содержимое обрезано)";

            return $"📄 {url}\n\n{text}";
        }
        catch (HttpRequestException ex)
        {
            var status = ex.StatusCode.HasValue ? $"{(int)ex.StatusCode.Value} " : "";
            return ToolResultFormatter.Error(
                "Ошибка web_read", $"не удалось загрузить страницу ({status}{ex.Message})",
                "проверь URL, доступность сайта или попробуй search для поиска альтернативной ссылки");
        }
        catch (UriFormatException ex)
        {
            return ToolResultFormatter.Error("Ошибка web_read", $"некорректный URL — {ex.Message}", "передай абсолютный URL");
        }
        catch (Exception ex)
        {
            return ToolResultFormatter.Error("Ошибка web_read", ex.Message, "попробуй другой URL или используй search");
        }
    }

    private static string? ExtractValidUrl(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return null;

        var match = UrlRegex.Match(input);
        if (match.Success)
        {
            var raw = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                return uri.ToString();
        }

        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            input = "https://" + input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
            return u.ToString();
        return null;
    }
}

public class WebReadToolOptions
{
    public const string SectionName = "WebRead";

    public int DefaultMaxLength { get; set; } = 8000;
}

public record WebReadArgs
{
    [JsonPropertyName("url")]
    [ToolParameter(Type = "string", Description = "URL страницы с документацией или информацией", Required = true)]
    public string? Url { get; init; }

    [JsonPropertyName("max_length")]
    [ToolParameter(Type = "number", Description = "Макс. символов для чтения")]
    public int? MaxLength { get; init; }
}
