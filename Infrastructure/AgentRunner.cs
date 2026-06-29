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

    private static readonly string[] KnownFileActions =
        ["read", "list", "glob", "read_tree", "write", "append", "delete", "edit", "create_dir", "delete_dir", "move", "copy"];

    public AgentRunner(
        IChatService chatService,
        ILogger<AgentRunner> logger,
        IOptions<AgentRunnerOptions> options)
    {
        _chatService = chatService;
        _logger = logger;
        _options = options.Value;
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
        var (messages, tools, toolLog, maxIterations) =
            BuildInitialContext(agent, context, userMessage, history);

        UsageInfo? totalUsage = null;
        int iteration = 0;
        AgentChatResponse? lastResponse = null;

        progress?.Report(new AgentRunnerProgress(Title: "Анализирую запрос", Step: 0));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;

            progress?.Report(new AgentRunnerProgress(Title: $"Итерация {iteration}", Step: iteration));

            var maxIterResult = CheckMaxIterations(agent, iteration, maxIterations, toolLog, totalUsage, lastResponse);
            if (maxIterResult != null) return maxIterResult;

            CompactMessages(messages);

            progress?.Report(new AgentRunnerProgress(Title: "Ожидаю ответ LLM...", Step: iteration, Status: "thinking"));
            var response = await _chatService.ChatWithMessagesAsync(messages, tools, model, cancellationToken);
            lastResponse = response;
            totalUsage = AddUsage(totalUsage, response.Usage);

            if (!string.IsNullOrEmpty(response.ReasoningContent))
                progress?.Report(new AgentRunnerProgress(Reasoning: response.ReasoningContent, Step: iteration));

            if (response.ToolCalls.Count == 0)
                return await HandleNoToolCallsResponseAsync(response, messages, model, toolLog, totalUsage, cancellationToken);

            AppendAssistantTurn(messages, response);
            await ExecuteToolCallsAsync(agent, response, messages, toolLog, progress, iteration, cancellationToken);

            InjectCorrectionIfNeeded(messages, toolLog);
        }
    }

    private (
        List<ApiMessage> Messages,
        List<ApiTool> Tools,
        List<string> ToolLog,
        int MaxIterations)
        BuildInitialContext(IAgent agent, AgentContext context, string userMessage, IReadOnlyList<ChatMessage>? history)
    {
        var systemPrompt = agent.GetSystemPrompt(context);
        var messages = new List<ApiMessage> { new("system", systemPrompt) };
        if (history != null)
            messages.AddRange(history.ToApiMessages());
        messages.Add(new("user", userMessage));

        var tools = agent.GetTools().Select(t => t.ToOpenAiTool()).ToList();
        var toolLog = new List<string>();

        var maxIterations = _options.MaxIterations > 0 ? _options.MaxIterations : 15;
        return (messages, tools, toolLog, maxIterations);
    }

    private AgentRunnerResult? CheckMaxIterations(
        IAgent agent, int iteration, int maxIterations,
        List<string> toolLog, UsageInfo? totalUsage, AgentChatResponse? lastResponse)
    {
        if (iteration <= maxIterations) return null;

        var msg = $"Достигнут лимит в {maxIterations} итераций. Выполнение прервано.";
        _logger.LogWarning("[Agent:{Agent}] MaxIterations reached ({Max}).", agent.GetType().Name, maxIterations);
        toolLog.Add($"⚠️ [Agent] Прерывание — {msg}");
        return new AgentRunnerResult(
            msg + " Возможно, инструменты работают некорректно или задача слишком сложна для автоматического решения.",
            toolLog,
            totalUsage,
            lastResponse?.ReasoningContent);
    }

    private async Task<AgentRunnerResult> HandleNoToolCallsResponseAsync(
        AgentChatResponse response,
        List<ApiMessage> messages,
        string? model,
        List<string> toolLog,
        UsageInfo? totalUsage,
        CancellationToken cancellationToken)
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

    private static void AppendAssistantTurn(List<ApiMessage> messages, AgentChatResponse response)
    {
        messages.Add(new ApiMessage("assistant",
            string.IsNullOrEmpty(response.Content) ? null : response.Content,
            ToolCalls: response.ToolCalls.Select(tc => new ApiToolCall(tc.Id, "function", new ApiFunctionCall(tc.Name, tc.ArgumentsJson ?? ""))).ToList(),
            ReasoningContent: response.ReasoningContent));
    }

    private async Task ExecuteToolCallsAsync(IAgent agent, AgentChatResponse response, List<ApiMessage> messages,
        List<string> toolLog, IProgress<AgentRunnerProgress>? progress, int iteration, CancellationToken cancellationToken)
    {
        var executed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in response.ToolCalls)
        {
            var args = tc.ArgumentsJson ?? "{}";
            var execKey = $"{tc.Name}:{args}";

            if (!executed.TryGetValue(execKey, out var result))
            {
                progress?.Report(new AgentRunnerProgress(
                    Title: $"Выполняю {tc.Name}...", Step: iteration, Tool: tc.Name));
                result = await ExecuteToolAsync(agent, tc.Name, args, cancellationToken);
                executed[execKey] = result;
                var formatted = FormatToolLogEntry(tc.Name, args, result);
                toolLog.Add(formatted);
                progress?.Report(new AgentRunnerProgress(
                    FormattedEntry: formatted, Step: iteration, Tool: tc.Name, Status: result.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase) ? "error" : "ok"));
            }

            messages.Add(new ApiMessage("tool", result, ToolCallId: tc.Id));
        }
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
                else
                {
                    // Preserve assistant intent, summarize tool results instead of dropping them
                    result.Add(messages[rounds[roundIdx].AssistantIndex]);
                    for (int j = rounds[roundIdx].StartIndex; j <= rounds[roundIdx].EndIndex; j++)
                    {
                        var summary = SummarizeToolResult(messages[j]);
                        result.Add(new ApiMessage("tool", summary, ToolCallId: messages[j].ToolCallId));
                    }
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

    private string FormatToolLogEntry(string name, string argsJson, string result)
    {
        var args = TryParseArgs(argsJson);
        string arg;
        string? action = null;

        if (name == "file")
        {
            // For file tool surface the action explicitly so the UI can
            // phase-classify reads vs. mutations and Russian-localise verbs.
            action = args.GetValueOrDefault("action");
            // Fallback: try to guess action from non-standard keys (e.g.
            // if the LLM sent {"path":"...","read":true} instead of
            // {"action":"read","path":"..."}). Without this, the UI would
            // show bare "file" and misclassify reads as actions.
            if (string.IsNullOrEmpty(action))
                action = KnownFileActions.FirstOrDefault(a => args.ContainsKey(a));
            // Guard against a bare "file" prefix when the LLM omitted the
            // action entirely. "unknown" keeps the log parseable and lets the
            // observer localise it instead of printing the raw tool name.
            if (string.IsNullOrEmpty(action))
                action = "unknown";
            if (action == "glob")
                arg = args.GetValueOrDefault("pattern") ?? args.GetValueOrDefault("path") ?? "";
            else
                arg = args.GetValueOrDefault("path")
                    ?? args.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v) && v != action)
                    ?? "";
        }
        else
        {
            arg = args.GetValueOrDefault("query") ?? args.GetValueOrDefault("command") ?? args.GetValueOrDefault("url")
                ?? args.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v))
                ?? "";
        }

        var argStr = string.IsNullOrEmpty(arg) ? "" : (arg.Length > 50 ? arg[..47] + "…" : arg);
        var prefix = action != null ? $"{name} {action}" : name;
        var argPart = string.IsNullOrEmpty(argStr) ? prefix : $"{prefix} {argStr}";

        var preview = result.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase)
            ? result
            : result.Length > 250 ? result[..247] + "…" : result;
        var oneLine = string.Join(" ", preview.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return $"{argPart} — {oneLine}";
    }

    private Dictionary<string, string> TryParseArgs(string json)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (d == null) return new Dictionary<string, string>();
            return d.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString() ?? "",
                JsonValueKind.Number => kv.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => kv.Value.GetRawText()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse tool arguments JSON: {Json}", json);
            return new Dictionary<string, string>();
        }
    }

    private static async Task<string> ExecuteToolAsync(IAgent agent, string name, string argumentsJson, CancellationToken ct)
    {
        var handler = agent.GetHandler(name);
        if (handler == null)
            return $"Неизвестный тул: {name}";
        var result = await handler.ExecuteAsync(argumentsJson, ct);
        return result.FormattedContent;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void InjectCorrectionIfNeeded(List<ApiMessage> messages, List<string> toolLog)
    {
        // Look at the last 5 tool-log entries:
        //   - keep the latest error per distinct tool for the targeted prompt;
        //   - count TOTAL occurrences so we fire on repсаeat-error too (not just distinct tools).
        var errorEntries = new List<(string Entry, string ToolName)>();
        int totalRecentErrors = 0;
        for (int i = toolLog.Count - 1; i >= 0 && i >= toolLog.Count - 5; i--)
        {
            if (toolLog[i].Contains("Ошибка", StringComparison.OrdinalIgnoreCase))
            {
                totalRecentErrors++;
                var spaceIdx = toolLog[i].IndexOf(' ');
                var toolName = spaceIdx > 0 ? toolLog[i][..spaceIdx].Trim() : "unknown";
                if (!errorEntries.Any(e => e.ToolName == toolName))
                    errorEntries.Add((toolLog[i], toolName));
            }
        }

        // Fire on either: 2+ distinct tools erroring, OR the same tool erroring
        // 2+ times in a row. The third-repeated error from the previous run
        // (e.g. glob-without-path three times) is a real loop the LLM needs to break.
        if (errorEntries.Count < 2 && totalRecentErrors < 2) return;

        var correctionPrompt = BuildTargetedCorrection(errorEntries);
        messages.Add(new ApiMessage("user", correctionPrompt));
        _logger.LogInformation("[Agent] Targeted correction injected ({Distinct} distinct / {Total} total recent errors)",
            errorEntries.Count, totalRecentErrors);
    }

    private static string BuildTargetedCorrection(List<(string Entry, string ToolName)> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("⚠️ Последние попытки завершились ошибками. ОСТАНОВИСЬ и пересмотри подход.");
        sb.AppendLine();

        var seenTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, toolName) in errors)
        {
            if (!seenTools.Add(toolName)) continue;

            if (toolName == "file" && entry.Contains("Текст", StringComparison.OrdinalIgnoreCase) && entry.Contains("не найден", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("📝 file edit: текст не найден в файле.");
                sb.AppendLine("   → Сначала вызови file read для этого файла.");
                sb.AppendLine("   → СКОПИРУЙ точный текст (включая пробелы и отступы) из вывода read.");
                sb.AppendLine("   → Вставь скопированный текст в SEARCH-блок.");
                sb.AppendLine("   → НЕ угадывай содержимое файла.");
            }
            else if (toolName == "file" && entry.Contains("укажите path", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("📁 file: забыл указать 'path'.");
                sb.AppendLine("   → path ОБЯЗАТЕЛЕН для всех action КРОМЕ 'list' (для list path=\".\" подразумевается).");
                sb.AppendLine("   → Для glob: передавай ОБА path (корневая папка) И pattern (маска).");
                sb.AppendLine("     Пример: {action:\"glob\", path:\"Agents\", pattern:\"**/*.cs\"}");
                sb.AppendLine("   → Для read/list/glob/etc.: path — относительный путь к файлу/папке от корня проекта.");
                sb.AppendLine("   → НЕ ВЫЗЫВАЙ glob без path — ты уже пробовал, и это не пройдёт. Перечитай сигнатуру тула.");
            }
            else if (toolName == "file" && entry.Contains("не найден", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("📁 file: файл/директория не найден(а).");
                sb.AppendLine("   → Вызови file list в родительской директории, проверь точное имя.");
                sb.AppendLine("   → Используй file glob для поиска по маске.");
                sb.AppendLine("   → НЕ повторяй тот же путь — он не существует.");
            }
            else if (toolName == "terminal")
            {
                sb.AppendLine("💻 terminal: команда завершилась с ошибкой.");
                sb.AppendLine("   → Внимательно прочитай stderr в выводе. Исправь синтаксис.");
                sb.AppendLine("   → На Windows используй PowerShell-синтаксис (-Switch вместо /switch).");
                sb.AppendLine("   → Проверь, что все файлы/пути в команде существуют.");
                sb.AppendLine("   → НЕ повторяй ту же команду без изменений.");
            }
            else if (toolName == "file")
            {
                sb.AppendLine("📁 file: операция не удалась.");
                sb.AppendLine("   → Прочитай сообщение об ошибке и подсказку в нём.");
                sb.AppendLine("   → Проверь путь через file list.");
                sb.AppendLine("   → Для edit: убедись, что SEARCH-блок содержит точный текст из файла.");
            }
            else if (toolName == "web_read")
            {
                sb.AppendLine("🌐 web_read: не удалось загрузить страницу.");
                sb.AppendLine("   → Проверь URL (должен начинаться с http:// или https://).");
                sb.AppendLine("   → Попробуй другой URL или используй search для поиска альтернативы.");
                sb.AppendLine("   → Некоторые сайты блокируют автоматическое чтение — попробуй зеркало.");
            }
            else if (toolName == "search")
            {
                sb.AppendLine("🔍 search: поиск не дал результатов.");
                sb.AppendLine("   → Переформулируй запрос (короче, другими словами).");
                sb.AppendLine("   → Попробуй web_read с известным URL документации.");
            }
            else
            {
                sb.AppendLine($"⚡ {toolName}: ошибка при выполнении.");
                sb.AppendLine("   → Прочитай сообщение об ошибке и попробуй другой подход.");
            }
            sb.AppendLine();
        }

        sb.Append("План: кратко опиши, что ты изменишь в подходе, и действуй.");
        return sb.ToString();
    }

    private static string SummarizeToolResult(ApiMessage toolMsg)
    {
        var content = toolMsg.Content ?? "";
        if (content.Length <= 200)
            return content;

        if (content.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase))
        {
            // Keep error info compact
            var firstLine = content.Split('\n')[0];
            return content.Length > 300 ? firstLine + $"\n... (ошибка, {content.Length} символов)" : content;
        }

        // File tool results
        if (content.StartsWith("✓ Успешно заменено", StringComparison.OrdinalIgnoreCase))
            return TruncateLines(content, 4);
        if (content.StartsWith("Записано ", StringComparison.OrdinalIgnoreCase))
            return TruncateTo(content, 100);
        if (content.StartsWith("Создана папка ", StringComparison.OrdinalIgnoreCase))
            return TruncateTo(content, 100);
        if (content.StartsWith("Удалён ", StringComparison.OrdinalIgnoreCase) || content.StartsWith("Удалена папка ", StringComparison.OrdinalIgnoreCase))
            return TruncateTo(content, 100);
        if (content.StartsWith("Файл перемещён", StringComparison.OrdinalIgnoreCase) || content.StartsWith("Папка перемещена", StringComparison.OrdinalIgnoreCase))
            return TruncateTo(content, 100);
        if (content.StartsWith("Файл скопирован", StringComparison.OrdinalIgnoreCase) || content.StartsWith("Папка скопирована", StringComparison.OrdinalIgnoreCase))
            return TruncateTo(content, 100);
        if (content.StartsWith("Добавлено ", StringComparison.OrdinalIgnoreCase))
            return TruncateTo(content, 100);

        // File list result — keep structure
        if (content.Contains('\n') && content.Split('\n').Length >= 3)
        {
            var lines = content.Split('\n');
            // Looks like a directory tree or file list
            if (lines.Length <= 15)
                return content;
            // Trim to first 10 lines + summary
            return string.Join('\n', lines.Take(10)) +
                   $"\n... (всего {lines.Length} строк, результат сжат для экономии контекста)";
        }

        // Terminal output
        if (content.Contains("--- stderr ---", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("код выхода", StringComparison.OrdinalIgnoreCase))
        {
            var firstPart = content.Split("--- stderr ---")[0].Trim();
            return firstPart.Length > 300 ? firstPart[..300] + "..." : firstPart;
        }

        // Search results
        if (content.Contains("📄 ", StringComparison.OrdinalIgnoreCase) && content.Contains("🔗 ", StringComparison.OrdinalIgnoreCase))
        {
            // Extract URLs and first lines
            var summary = new StringBuilder();
            summary.AppendLine("(результаты поиска сжаты)");
            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("🔗 ") || line.StartsWith("📄 "))
                    summary.AppendLine(line);
            }
            return summary.ToString().Trim();
        }

        // Web read results
        if (content.StartsWith("📄 http", StringComparison.OrdinalIgnoreCase))
        {
            var firstLine = content.Split('\n')[0];
            return firstLine + $"\n... (содержимое страницы сжато, {content.Length} символов)";
        }

        // Default: truncate
        return TruncateTo(content, 250) + $"\n... ({content.Length} символов сжато)";
    }

    private static string TruncateTo(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static string TruncateLines(string s, int maxLines)
    {
        var lines = s.Split('\n');
        if (lines.Length <= maxLines) return s;
        return string.Join('\n', lines.Take(maxLines)) + $"\n... (ещё {lines.Length - maxLines} строк)";
    }

}
