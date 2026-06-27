using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Click.Infrastructure;

public class AgentRunner : IAgentRunner
{
    private readonly IChatService _chatService;
    private readonly ILogger<AgentRunner> _logger;
    private readonly AgentRunnerOptions _options;

    private static readonly Regex InternalTokensRegex = new(
        @"<\|[^|]+\|>|\[INST\]|\[/INST\]| to=functions\.\w+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public AgentRunner(
        IChatService chatService,
        ILogger<AgentRunner> logger,
        IOptions<AgentRunnerOptions> options)
    {
        _chatService = chatService;
        _logger = logger;
        _options = options.Value;
    }

    private static UsageInfo? AddUsage(UsageInfo? acc, UsageInfo? next)
    {
        if (next is null) return acc;
        if (acc is null) return next;
        return new UsageInfo(
            acc.PromptTokens + next.PromptTokens,
            acc.CompletionTokens + next.CompletionTokens,
            acc.TotalTokens + next.TotalTokens);
    }

    private void CompactMessages(List<ApiMessage> messages)
    {
        var maxToolLength = _options.MaxToolResultCharsKeep > 0 ? _options.MaxToolResultCharsKeep : 2500;
        var maxSuccessMax = _options.MaxToolResultCharsSuccess > 0 ? _options.MaxToolResultCharsSuccess : 400;
        var preserveRounds = _options.PreserveRecentToolRounds > 0 ? _options.PreserveRecentToolRounds : 2;

        var rounds = new List<(int AssistantIndex, int StartIndex, int EndIndex)>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == "assistant" && messages[i].ToolCalls?.Count > 0)
            {
                int end = i;
                while (end + 1 < messages.Count && messages[end + 1].Role == "tool")
                    end++;
                rounds.Add((i, i + 1, end));
            }
        }

        var keep = new HashSet<int>(Enumerable.Range(
            Math.Max(0, rounds.Count - preserveRounds),
            Math.Min(preserveRounds, rounds.Count)));

        var result = new List<ApiMessage>(messages.Count);
        int roundIdx = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            if (roundIdx < rounds.Count && rounds[roundIdx].AssistantIndex == i)
            {
                if (keep.Contains(roundIdx))
                {
                    for (int j = rounds[roundIdx].AssistantIndex; j <= rounds[roundIdx].EndIndex; j++)
                        result.Add(messages[j]);
                }

                i = rounds[roundIdx].EndIndex;
                roundIdx++;
                continue;
            }

            result.Add(messages[i]);
        }

        messages.Clear();
        messages.AddRange(result);

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == "tool" && (messages[i].Content?.Length ?? 0) > maxToolLength)
            {
                var c = messages[i].Content!;
                bool isError = c.Contains("Ошибка", StringComparison.OrdinalIgnoreCase)
                    || c.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || c.Contains("stderr", StringComparison.OrdinalIgnoreCase);
                if (isError)
                {
                    var tail = c.Length > maxToolLength ? c[^maxToolLength..] : c;
                    messages[i] = messages[i] with { Content = $"[... truncated error tail ...]\n{tail}" };
                }
                else
                {
                    messages[i] = messages[i] with { Content = c[..maxSuccessMax] + $"\n[... {c.Length - maxSuccessMax} chars truncated by context limit ...]" };
                }
            }
        }
    }

    public async Task<AgentRunnerResult> RunAsync(
        IAgent agent,
        AgentContext context,
        string userMessage,
        IReadOnlyList<ChatMessage>? history = null,
        string? model = null,
        IProgress<AgentRunnerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = agent.GetSystemPrompt(context);

        var messages = new List<ApiMessage> { new("system", systemPrompt) };
        if (history != null)
            messages.AddRange(history.ToApiMessages());
        messages.Add(new("user", userMessage));

        var tools = agent.GetTools().Select(t => t.ToOpenAiTool()).ToList();
        var toolLog = new List<string>();
        UsageInfo? totalUsage = null;

        var maxIterations = _options.MaxIterations > 0 ? _options.MaxIterations : 15;
        var loopWindow = _options.LoopDetectionWindow > 0 ? _options.LoopDetectionWindow : 2;
        var recentFingerprints = new Queue<string>(loopWindow);

        int iteration = 0;
        AgentChatResponse? lastResponse = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;

            if (iteration > maxIterations)
            {
                var msg = $"Достигнут лимит в {maxIterations} итераций. Выполнение прервано.";
                _logger.LogWarning("[Agent:{Agent}] MaxIterations reached ({Max}).", agent.GetType().Name, maxIterations);
                toolLog.Add($"⚠️ [Agent] Прерывание — {msg}");
                return new AgentRunnerResult(
                    msg + " Возможно, инструменты работают некорректно или задача слишком сложна для автоматического решения.",
                    toolLog,
                    totalUsage,
                    lastResponse?.ReasoningContent);
            }

            CompactMessages(messages);
            var response = await _chatService.ChatWithMessagesAsync(messages, tools, model, cancellationToken);
            lastResponse = response;
            totalUsage = AddUsage(totalUsage, response.Usage);

            if (response.ToolCalls.Count == 0)
            {
                var content = CleanContent(response.Content?.Trim() ?? "");
                if (string.IsNullOrEmpty(content) && toolLog.Count > 0)
                {
                    try
                    {
                        var (summaryContent, summaryUsage) = await RequestSummarizationAsync(messages, model, toolLog, cancellationToken);
                        totalUsage = AddUsage(totalUsage, summaryUsage);
                        content = CleanContent(summaryContent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Summarization fallback failed");
                    }
                }
                return new AgentRunnerResult(
                    string.IsNullOrEmpty(content) ? "Извини, не смог подготовить ответ. Попробуй переформулировать вопрос." : content,
                    toolLog,
                    totalUsage,
                    response.ReasoningContent);
            }

            // Build fingerprint BEFORE any tool side-effect so loop detection short-circuits cheaply.
            var iterationKeys = new List<string>(response.ToolCalls.Count);
            foreach (var tc in response.ToolCalls)
            {
                var normalizedArgs = WhitespaceRegex.Replace(tc.ArgumentsJson ?? "", "");
                iterationKeys.Add($"{tc.Name}:{normalizedArgs}");
            }
            iterationKeys.Sort(StringComparer.Ordinal);
            var currentFingerprint = string.Join("||", iterationKeys);

            if (recentFingerprints.Contains(currentFingerprint))
            {
                var msg = "Агент зациклился на одинаковых вызовах инструментов. Выполнение прерывается.";
                _logger.LogWarning(
                    "[Agent:{Agent}] Loop detected. Fingerprint: {Fingerprint}",
                    agent.GetType().Name, Truncate(currentFingerprint, 200));
                toolLog.Add($"🔁 [Agent] Зацикливание — {msg}");
                return new AgentRunnerResult(
                    msg + " Попробуй переформулировать запрос или изменить подход.",
                    toolLog,
                    totalUsage,
                    response.ReasoningContent);
            }

            recentFingerprints.Enqueue(currentFingerprint);
            while (recentFingerprints.Count > loopWindow)
                recentFingerprints.Dequeue();

            // Assistant turn with all announced tool calls (triggers fingerprint disclosure to LLM).
            messages.Add(new ApiMessage("assistant", string.IsNullOrEmpty(response.Content) ? null : response.Content,
                ToolCalls: response.ToolCalls.Select(tc => new ApiToolCall(tc.Id, "function", new ApiFunctionCall(tc.Name, tc.ArgumentsJson ?? ""))).ToList()));

            // Execute tools and append results.
            var executed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tc in response.ToolCalls)
            {
                var args = tc.ArgumentsJson ?? "{}";
                var execKey = $"{tc.Name}:{args}";

                if (!executed.TryGetValue(execKey, out var result))
                {
                    result = await ExecuteToolAsync(agent, tc.Name, args, cancellationToken);
                    executed[execKey] = result;
                    var formatted = FormatToolLogEntry(tc.Name, args, result);
                    toolLog.Add(formatted);
                }

                messages.Add(new ApiMessage("tool", result, ToolCallId: tc.Id));
            }
        }
    }

    private async Task<(string Content, UsageInfo? Usage)> RequestSummarizationAsync(
        List<ApiMessage> messages,
        string? model,
        List<string> toolLog,
        CancellationToken ct)
    {
        var actionsSummary = new StringBuilder();
        actionsSummary.AppendLine("Я выполнил следующие действия:");
        actionsSummary.AppendLine();

        for (int i = 0; i < toolLog.Count; i++)
        {
            var entry = toolLog[i];
            actionsSummary.AppendLine($"{i + 1}. {entry}");
        }

        actionsSummary.AppendLine();
        actionsSummary.AppendLine("Теперь дай пользователю развернутый ответ на русском языке, объяснив что было сделано и какие результаты получены.");

        messages.Add(new ApiMessage("user", actionsSummary.ToString()));
        var response = await _chatService.ChatWithMessagesAsync(messages, tools: null, model, ct);
        return (response.Content?.Trim() ?? "", response.Usage);
    }

    private static string CleanContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        var cleaned = InternalTokensRegex.Replace(content, "").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? content : cleaned;
    }

    private static string FormatToolLogEntry(string name, string argsJson, string result)
    {
        var args = TryParseArgs(argsJson);
        var arg = args.GetValueOrDefault("query") ?? args.GetValueOrDefault("command") ?? args.GetValueOrDefault("url")
            ?? args.GetValueOrDefault("path")
            ?? args.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var argStr = string.IsNullOrEmpty(arg) ? "" : (arg.Length > 50 ? arg[..47] + "…" : arg);
        var argPart = string.IsNullOrEmpty(argStr) ? name : $"{name} {argStr}";
        var preview = result.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase)
            ? result
            : result.Length > 250 ? result[..247] + "…" : result;
        var oneLine = string.Join(" ", preview.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return $"{argPart} — {oneLine}";
    }

    private static Dictionary<string, string> TryParseArgs(string json)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (d == null) return new Dictionary<string, string>();
            return d.ToDictionary(kv => kv.Key, kv => kv.Value.GetString() ?? "");
        }
        catch { return new Dictionary<string, string>(); }
    }

    private static async Task<string> ExecuteToolAsync(IAgent agent, string name, string argumentsJson, CancellationToken ct)
    {
        var handler = agent.GetHandler(name);
        if (handler == null)
            return $"Неизвестный тул: {name}";
        var result = await handler.ExecuteAsync(argumentsJson, ct);
        return result ?? $"Ошибка выполнения {name}";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
