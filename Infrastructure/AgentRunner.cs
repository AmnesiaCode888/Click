using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSharp;

namespace Click.Infrastructure;

public class AgentRunner : IAgentRunner
{
    private readonly IChatService _chatService;

    private static readonly Regex InternalTokensRegex = new(
        @"<\|[^|]+\|>|\[INST\]|\[/INST\]| to=functions\.\w+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AgentRunner(IChatService chatService)
    {
        _chatService = chatService;
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

    private static void CompactMessages(List<ApiMessage> messages, int preserveRecentToolRounds = 2)
    {
        const int maxToolLength = 2500;

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
            Math.Max(0, rounds.Count - preserveRecentToolRounds),
            Math.Min(preserveRecentToolRounds, rounds.Count)));

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
                    messages[i] = messages[i] with { Content = c[..400] + $"\n[... {c.Length - 400} chars truncated by context limit ...]" };
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CompactMessages(messages);
            var response = await _chatService.ChatWithMessagesAsync(messages, tools, model, cancellationToken);
            totalUsage = AddUsage(totalUsage, response.Usage);

            if (response.ToolCalls.Count == 0)
            {
                var content = CleanContent(response.Content?.Trim() ?? "");
                if (string.IsNullOrEmpty(content) && toolLog.Count > 0)
                {
                    var (summaryContent, summaryUsage) = await RequestSummarizationAsync(messages, model, toolLog, cancellationToken);
                    totalUsage = AddUsage(totalUsage, summaryUsage);
                    content = CleanContent(summaryContent);
                }
                return new AgentRunnerResult(
                    string.IsNullOrEmpty(content) ? "Извини, не смог подготовить ответ. Попробуй переформулировать вопрос." : content,
                    toolLog,
                    totalUsage,
                    response.ReasoningContent);
            }

            var executed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            messages.Add(new ApiMessage("assistant", string.IsNullOrEmpty(response.Content) ? null : response.Content,
                ToolCalls: response.ToolCalls.Select(tc => new ApiToolCall(tc.Id, "function", new ApiFunctionCall(tc.Name, tc.ArgumentsJson))).ToList()));

            foreach (var tc in response.ToolCalls)
            {
                var key = $"{tc.Name}:{tc.ArgumentsJson}";

                if (!executed.TryGetValue(key, out var result))
                {
                    result = await ExecuteToolAsync(agent, tc.Name, tc.ArgumentsJson, cancellationToken);
                    executed[key] = result;
                    var formatted = FormatToolLogEntry(tc.Name, tc.ArgumentsJson, result);
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
}
