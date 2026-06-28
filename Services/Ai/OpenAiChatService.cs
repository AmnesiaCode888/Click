using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentSharp;

namespace Click;

public class OpenAiChatService : IChatService
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly SemaphoreSlim _modelsCacheLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex InlineToolCallMarkerRegex = new(
        @"functions\.(?<name>[a-zA-Z_][a-zA-Z0-9_]*)(?::(?<index>\d+))?\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string[]? _cachedModels;
    private DateTimeOffset _cacheExpiresAt;

    public OpenAiChatService(HttpClient httpClient, OpenAiOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        // Double-checked locking pattern: read fast without lock, then re-check under semaphore on miss.
        if (_cachedModels != null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            return _cachedModels;

        await _modelsCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedModels != null && DateTimeOffset.UtcNow < _cacheExpiresAt)
                return _cachedModels;

            return await FetchModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _modelsCacheLock.Release();
        }
    }

    private async Task<string[]> FetchModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = _options.BaseUrl.TrimEnd('/') + "/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            SetAuthHeaders(req, _options.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        list.Add(id.GetString()!);
                }
                _cachedModels = list.ToArray();
                _cacheExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
                return _cachedModels;
            }
        }
        catch { }

        return Array.Empty<string>();
    }

    public async Task<AgentChatResponse> ChatAsync(
        string userMessage,
        IReadOnlyList<ChatMessage>? history = null,
        IReadOnlyList<ApiTool>? tools = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ApiMessage>();
        if (history != null)
            messages.AddRange(history.ToApiMessages());
        messages.Add(new ApiMessage("user", userMessage));
        return await ChatWithMessagesAsync(messages, tools, model, cancellationToken);
    }

    public async Task<AgentChatResponse> ChatWithMessagesAsync(
        IReadOnlyList<ApiMessage> messages,
        IReadOnlyList<ApiTool>? tools = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, _options.RetryMaxAttempts);
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await SendSingleRequestAsync(messages, tools, model, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                lastEx = new TimeoutException("Request timed out");
                await Task.Delay(ComputeDelay(attempt, _options.RetryBaseDelayMs, isTimeout: true), cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                await Task.Delay(ComputeDelay(attempt, _options.RetryBaseDelayMs, isTimeout: false), cancellationToken);
            }
            catch (InvalidOperationException ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                var delay = TryExtractRetryAfterDelay(ex, attempt);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                await Task.Delay(ComputeDelay(attempt, _options.RetryBaseDelayMs, isTimeout: false), cancellationToken);
            }
        }

        throw lastEx ?? new InvalidOperationException("Failed to get response from API");
    }

    private async Task<AgentChatResponse> SendSingleRequestAsync(
        IReadOnlyList<ApiMessage> messages,
        IReadOnlyList<ApiTool>? tools,
        string? model,
        CancellationToken cancellationToken)
    {
        var (baseUrl, apiKey, effectiveModel) = ResolveEndpointAndModel(model);
        var request = new ChatRequest(
            Model: effectiveModel,
            Messages: messages,
            MaxTokens: _options.MaxTokens,
            Stream: true,
            Tools: tools?.Count > 0 ? tools : null,
            ParallelToolCalls: tools?.Count > 0 && _options.UseParallelToolCalls ? true : null,
            ToolChoice: tools?.Count > 0 && _options.UseRequiredToolChoice ? "required" as object : null
        );

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        SetAuthHeaders(req, apiKey);
        if (_options.AdditionalHeaders != null)
        {
            foreach (var h in _options.AdditionalHeaders)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        req.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await _httpClient.SendAsync(req, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            throw new InvalidOperationException($"API error {statusCode}: {body}|{retryAfter?.TotalSeconds ?? 0}");
        }

        return await ParseStreamedResponseAsync(response, cts.Token);
    }

    private static TimeSpan ComputeDelay(int attempt, int baseDelayMs, bool isTimeout)
    {
        var jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
        var delayMs = isTimeout
            ? baseDelayMs * attempt
            : baseDelayMs * (int)Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(delayMs * jitter);
    }

    private static TimeSpan TryExtractRetryAfterDelay(InvalidOperationException ex, int attempt)
    {
        var message = ex.Message;
        var pipeIdx = message.LastIndexOf('|');
        if (pipeIdx > 0 && double.TryParse(message[(pipeIdx + 1)..], out var seconds) && seconds > 0)
        {
            var cap = TimeSpan.FromSeconds(30);
            var delay = TimeSpan.FromSeconds(Math.Min(seconds, cap.TotalSeconds));
            return delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        }
        return ComputeDelay(attempt, 2000, isTimeout: false);
    }

    private static void SetAuthHeaders(HttpRequestMessage req, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return;
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    private (string BaseUrl, string? ApiKey, string EffectiveModel) ResolveEndpointAndModel(string? model)
    {
        var m = model ?? _options.Model;

        if (m.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
        {
            var id = m.Split('/', 2)[1];
            var baseUrl = string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)
                ? "http://localhost:11434/v1"
                : _options.OllamaBaseUrl!;
            return (baseUrl, _options.OllamaApiKey, id);
        }

        if (m.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
        {
            var id = m.Split('/', 2)[1];
            var baseUrl = string.IsNullOrWhiteSpace(_options.LmStudioBaseUrl)
                ? "http://localhost:1234/v1"
                : _options.LmStudioBaseUrl!;
            return (baseUrl, _options.LmStudioApiKey, id);
        }

        return (_options.BaseUrl, _options.ApiKey, m);
    }

    private static async Task<AgentChatResponse> ParseStreamedResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var contentBuilder = new StringBuilder();
        var toolCallMap = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? reasoningContent = null;
        UsageInfo? usage = null;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data: "))
                continue;

            var data = line.Substring(6);
            if (data == "[DONE]")
                break;

            try
            {
                using var chunk = JsonDocument.Parse(data);
                var root = chunk.RootElement;

                if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
                {
                    usage = ParseUsage(usageProp);
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];
                var delta = choice.GetProperty("delta");

                if (delta.TryGetProperty("content", out var contentProp))
                {
                    var text = contentProp.GetString();
                    if (!string.IsNullOrEmpty(text))
                        contentBuilder.Append(text);
                }

                if (delta.TryGetProperty("reasoning_content", out var rc))
                {
                    var r = rc.GetString();
                    if (!string.IsNullOrEmpty(r))
                        reasoningContent = (reasoningContent ?? "") + r;
                }
                else if (delta.TryGetProperty("reasoning", out var rProp))
                {
                    var r = rProp.GetString();
                    if (!string.IsNullOrEmpty(r))
                        reasoningContent = (reasoningContent ?? "") + r;
                }

                if (delta.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array && tcArray.GetArrayLength() > 0)
                {
                    foreach (var tc in tcArray.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : 0;

                        if (!toolCallMap.ContainsKey(index))
                            toolCallMap[index] = ("", "", new StringBuilder());

                        if (tc.TryGetProperty("id", out var idProp))
                        {
                            var (_, name, args) = toolCallMap[index];
                            toolCallMap[index] = (idProp.GetString() ?? "", name, args);
                        }

                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var nameProp))
                            {
                                var (id, _, args) = toolCallMap[index];
                                toolCallMap[index] = (id, nameProp.GetString() ?? "", args);
                            }
                            if (fn.TryGetProperty("arguments", out var argsProp))
                            {
                                var argsText = argsProp.GetString();
                                if (!string.IsNullOrEmpty(argsText))
                                    toolCallMap[index].Args.Append(argsText);
                            }
                        }
                    }
                }
            }
            catch (JsonException) { }
        }

        var toolCalls = new List<ToolCallRequest>();
        int generatedIdIndex = 0;
        foreach (var (_, (id, name, args)) in toolCallMap.OrderBy(x => x.Key))
        {
            var arguments = args.ToString();
            if (!IsValidJsonObject(arguments))
                continue;

            var finalId = string.IsNullOrEmpty(id) ? $"call_{generatedIdIndex++}" : id;
            toolCalls.Add(new ToolCallRequest(finalId, name, arguments));
        }

        var content = contentBuilder.ToString();
        if (toolCalls.Count == 0)
        {
            var (cleanedContent, inlineToolCalls) = ExtractInlineToolCalls(content);
            if (inlineToolCalls.Count > 0)
            {
                content = cleanedContent;
                toolCalls.AddRange(inlineToolCalls);
            }
        }

        return new AgentChatResponse(content, toolCalls, usage, reasoningContent);
    }

    private static (string CleanedContent, List<ToolCallRequest> ToolCalls) ExtractInlineToolCalls(string content)
    {
        var toolCalls = new List<ToolCallRequest>();
        if (string.IsNullOrEmpty(content))
            return (content, toolCalls);

        var cleaned = new StringBuilder();
        int position = 0;
        int foundIndex = 0;

        while (position < content.Length)
        {
            var match = InlineToolCallMarkerRegex.Match(content, position);
            if (!match.Success)
            {
                cleaned.Append(content[position..]);
                break;
            }

            cleaned.Append(content.Substring(position, match.Index - position));

            var name = match.Groups["name"].Value;
            var argsJson = ExtractJsonObject(content, match.Index + match.Length - 1);

            if (!string.IsNullOrEmpty(argsJson) && IsValidJsonObject(argsJson))
            {
                var id = $"inline_{foundIndex}";
                toolCalls.Add(new ToolCallRequest(id, name, argsJson));
                foundIndex++;
                position = match.Index + match.Length - 1 + argsJson.Length;
            }
            else
            {
                cleaned.Append(match.Value);
                position = match.Index + match.Length;
            }
        }

        return (cleaned.ToString().Trim(), toolCalls);
    }

    private static string? ExtractJsonObject(string text, int startIndex)
    {
        if (startIndex >= text.Length || text[startIndex] != '{')
            return null;

        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = startIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text.Substring(startIndex, i - startIndex + 1);
                }
            }
        }

        return null;
    }

    private static bool IsValidJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static UsageInfo? ParseUsage(JsonElement usageProp)
    {
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;

        if (usageProp.TryGetProperty("prompt_tokens", out var pt))
            promptTokens = pt.GetInt32();
        if (usageProp.TryGetProperty("completion_tokens", out var ct))
            completionTokens = ct.GetInt32();
        if (usageProp.TryGetProperty("total_tokens", out var tt))
            totalTokens = tt.GetInt32();

        if (promptTokens == null && completionTokens == null && totalTokens == null)
            return null;

        return new UsageInfo(
            promptTokens ?? 0,
            completionTokens ?? 0,
            totalTokens ?? (promptTokens.GetValueOrDefault() + completionTokens.GetValueOrDefault()));
    }
}
