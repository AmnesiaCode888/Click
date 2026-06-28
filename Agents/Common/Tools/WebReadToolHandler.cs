using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
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

    public async Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ToolArgumentValidator.TryValidateJson<WebReadArgs>(argumentsJson, out var args, out var err) || args == null)
                return ToolResult.FromString(ToolResultFormatter.Error("Ошибка web_read", "неверные arguments для web_read", err));

            if (string.IsNullOrWhiteSpace(args.Url))
                return ToolResult.FromString(ToolResultFormatter.Error(
                    "Ошибка web_read", "укажите url",
                    "передай абсолютный URL, например https://example.com/docs"));

            var url = ExtractValidUrl(args.Url.Trim());
            if (string.IsNullOrEmpty(url))
                return ToolResult.FromString(ToolResultFormatter.Error(
                    "Ошибка web_read", $"некорректный URL '{args.Url}'",
                    "URL должен начинаться с http:// или https://"));

            // Check cache first
            if (_cache.TryGetValue(url, out var cached))
            {
                _logger.LogInformation("[Read] {Url} (cached)", url);
                var maxLenCache = args.MaxLength ?? _options.DefaultMaxLength;
                var cachedText = cached.Length > maxLenCache ? cached[..maxLenCache] + "\n... (содержимое обрезано)" : cached;
                var cachedFormatted = $"📄 {url} [из кэша]\n\n{cachedText}";
                return ToolResult.Structured(new { Url = url, Content = cached, FromCache = true }, cachedFormatted);
            }

            _logger.LogInformation("[Read] {Url}", url);

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Extract main content: prefer <article>, <main>, or the body
            var contentNode = doc.DocumentNode.SelectSingleNode("//article")
                ?? doc.DocumentNode.SelectSingleNode("//main")
                ?? doc.DocumentNode.SelectSingleNode("//body")
                ?? doc.DocumentNode;

            // Remove non-content elements
            foreach (var node in contentNode.SelectNodes(".//script|.//style|.//noscript|.//nav|.//header|.//footer|.//aside") ?? Enumerable.Empty<HtmlNode>())
                node.Remove();

            // Convert HTML to Markdown-like text
            var text = ConvertToMarkdown(contentNode, url);

            var maxLen = args.MaxLength ?? _options.DefaultMaxLength;
            if (text.Length > maxLen)
                text = text[..maxLen] + "\n... (содержимое обрезано)";

            // Cache the result
            _cache[url] = text;

            var formatted = $"📄 {url}\n\n{text}";
            return ToolResult.Structured(new { Url = url, Content = text }, formatted);
        }
        catch (HttpRequestException ex)
        {
            var status = ex.StatusCode.HasValue ? $"{(int)ex.StatusCode.Value} " : "";
            return ToolResult.FromString(ToolResultFormatter.Error(
                "Ошибка web_read", $"не удалось загрузить страницу ({status}{ex.Message})",
                "проверь URL, доступность сайта или попробуй search для поиска альтернативной ссылки"));
        }
        catch (UriFormatException ex)
        {
            return ToolResult.FromString(ToolResultFormatter.Error("Ошибка web_read", $"некорректный URL — {ex.Message}", "передай абсолютный URL"));
        }
        catch (Exception ex)
        {
            return ToolResult.FromString(ToolResultFormatter.Error("Ошибка web_read", ex.Message, "попробуй другой URL или используй search"));
        }
    }

    private static string ConvertToMarkdown(HtmlNode node, string baseUrl)
    {
        var sb = new StringBuilder();
        ConvertNode(node, sb, baseUrl);
        // Clean up excessive whitespace
        var result = Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
        result = Regex.Replace(result, @"^\s+$\n", "", RegexOptions.Multiline);
        return result.Trim();
    }

    private static void ConvertNode(HtmlNode node, StringBuilder sb, string baseUrl)
    {
        switch (node.Name.ToLowerInvariant())
        {
            case "#text":
                var text = HtmlEntity.DeEntitize(node.InnerText);
                if (!string.IsNullOrWhiteSpace(text) || (sb.Length > 0 && sb[^1] != '\n'))
                    sb.Append(text);
                break;

            case "br":
                sb.AppendLine();
                break;

            case "p":
            case "div":
            case "section":
            case "article":
            case "main":
            case "li":
            case "blockquote":
                sb.AppendLine();
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                sb.AppendLine();
                break;

            case "h1":
                sb.Append("\n# ");
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                sb.AppendLine("\n");
                break;

            case "h2":
                sb.Append("\n## ");
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                sb.AppendLine("\n");
                break;

            case "h3":
                sb.Append("\n### ");
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                sb.AppendLine("\n");
                break;

            case "h4":
            case "h5":
            case "h6":
                sb.Append("\n#### ");
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                sb.AppendLine("\n");
                break;

            case "pre":
            case "code":
                sb.Append("\n```\n");
                sb.Append(HtmlEntity.DeEntitize(node.InnerText).Trim());
                sb.Append("\n```\n");
                break;

            case "a":
                var href = node.GetAttributeValue("href", "");
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                if (!string.IsNullOrEmpty(href) && !href.StartsWith("#"))
                {
                    if (!Uri.TryCreate(href, UriKind.Absolute, out _) &&
                        Uri.TryCreate(new Uri(baseUrl), href, out var resolved))
                        href = resolved.ToString();
                    sb.Append($" ({href})");
                }
                break;

            case "strong":
            case "b":
                sb.Append("**");
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                sb.Append("**");
                break;

            case "em":
            case "i":
                sb.Append("*");
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                sb.Append("*");
                break;

            case "ul":
            case "ol":
                sb.AppendLine();
                foreach (var child in node.ChildNodes)
                {
                    if (child.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append("- ");
                        foreach (var liChild in child.ChildNodes)
                            ConvertNode(liChild, sb, baseUrl);
                        sb.AppendLine();
                    }
                }
                break;

            case "img":
                var alt = node.GetAttributeValue("alt", "");
                var src = node.GetAttributeValue("src", "");
                sb.Append($"[Изображение: {alt}]");
                if (!string.IsNullOrEmpty(src))
                    sb.Append($" ({src})");
                break;

            case "hr":
                sb.AppendLine("\n---\n");
                break;

            default:
                foreach (var child in node.ChildNodes)
                    ConvertNode(child, sb, baseUrl);
                break;
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
