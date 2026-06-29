using AgentSharp;
using Spectre.Console;

namespace Click.Infrastructure;

/// <summary>
/// Phase-aware ReAct-loop progress reporter.
///
/// Renders tool calls / reasoning as a vertical timeline where phase headers
/// and their sub-items all share the same column, so the eye can glide down
/// each block without re-aligning:
///
///     ● Исследование
///     │ Прочитало Infrastructure/AgentRunner.cs — ...
///     │ Поиск по маске "**/*.cs" — 23 совпадения
///     ● Думает
///     │ Нужно проверить FileToolHandler.cs, чтобы понять как работает read...
///     ● Действие
///     │ Выполнило dotnet build — exit 0
///
/// Each phase opens with an empty connector bar (skipping the very first phase
/// so the tree starts flush under "Запускаю агент…"), giving the timeline a
/// continuous vertical thread between phases. Each sub-item keeps the same
/// leading gutter so the bar (│) lines up under the bullet (●). Inside one
/// phase sub-items render "verb + arg" only — content previews stay in the
/// hidden tool-log; the user-facing timeline is meant to read as a quick
/// summary, not a transcript.
/// </summary>
public sealed class RunObserver : IProgress<AgentRunnerProgress>
{
    private readonly object _lock = new();
    private string? _currentPhase;
    private bool _isFirstPhase = true;

    private const string PhaseResearch = "Исследование";
    private const string PhaseAction = "Действие";
    private const string PhaseThinking = "Думает";

    /// <summary>
    /// Both the phase bullet (●) and the sub-item bar (│) start at this
    /// column so they line up vertically when the agent stacks many sub-items
    /// under a single phase. Width is two spaces — wide enough to read as
    /// "indented under the timeline header" but narrow enough to leave room
    /// for long paths/queries.
    /// </summary>
    private const string Gutter = "  ";

    public void Report(AgentRunnerProgress value)
    {
        lock (_lock)
        {
            // 1) Tool completed: classify phase, open header if changed, then
            //    print a friendly Russian-localised sub-line.
            if (value.FormattedEntry != null)
            {
                var phase = ClassifyPhase(value.Tool, value.FormattedEntry);
                if (phase != _currentPhase) OpenPhase(phase);
                AnsiConsole.MarkupLine(SubLine(value.FormattedEntry, value.Status == "error"));
                return;
            }

            // 2) LLM reasoning: always the "Думает" phase. Distinct voice
            //    (italic dim cyan — same family as Исследование per user
            //    request but stylistically italic-dimmed to skip apart from
            //    the regular grey sub-items).
            if (!string.IsNullOrEmpty(value.Reasoning))
            {
                if (_currentPhase != PhaseThinking) OpenPhase(PhaseThinking);
                var collapsed = CollapseTrim(value.Reasoning, 320);
                AnsiConsole.MarkupLine($"{Gutter}│ [dim italic cyan]{Markup.Escape(collapsed)}[/]");
            }
        }
    }

    /// <summary>
    /// Open a new phase: connector bar (except for the very first phase, so
    /// the timeline starts flush under "Запускаю агент…"), then header. The
    /// connector bar matches the sub-item bars above so phases read as one
    /// continuous vertical spine rather than a list of disconnected blocks.
    /// </summary>
    private void OpenPhase(string phase)
    {
        if (!_isFirstPhase) AnsiConsole.MarkupLine($"{Gutter}│");
        AnsiConsole.MarkupLine(PhaseHeader(phase));
        _currentPhase = phase;
        _isFirstPhase = false;
    }

    /// <summary>
    /// Phase-coloured bullet. All three phases share the голубой / cyan
    /// family per user request — headers are distinguished by the word
    /// itself, sub-items by their body styling (italic dim vs. plain grey).
    /// </summary>
    private static string PhaseHeader(string phase)
    {
        // Голубой covers both Исследование and Думает; Действие stays in
        // its own family so it reads as "I took an action" vs. "I worked it
        // out" / "I read/searched".
        var colour = phase switch
        {
            PhaseAction => "yellow",
            _ => "cyan",
        };
        return $"{Gutter}[bold {colour}]● {Markup.Escape(phase)}[/]";
    }

    /// <summary>
    /// Sub-line: gutter + bar + verb (localised) + bold arg. Content
    /// previews are deliberately dropped on success — the timeline should
    /// read as a quick checklist of "what the agent did", not a transcript
    /// (full payloads stay in the hidden tool-log). Errors add a `✗`
    /// marker and a short, trimmed hint so failures stay scannable; the
    /// leading "Ошибка:" prefix is stripped because the red ✗ already
    /// signals failure.
    /// </summary>
    private static string SubLine(string formatted, bool isError)
    {
        var parts = ParseFormatted(formatted);
        var argMarkup = string.IsNullOrEmpty(parts.Arg) ? "" : $" [bold]{Markup.Escape(parts.Arg)}[/]";
        if (isError)
        {
            var hint = parts.Rest;
            const string ErrorPrefix = "Ошибка";
            if (hint.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Drop "Ошибка" / "Ошибка:" / "Ошибка: ..." — the ✗ glyph
                // already marks failure, so duplicating the word in the
                // hint is noise.
                hint = hint[ErrorPrefix.Length..].TrimStart(' ', ':', '—', '-');
            }
            hint = CollapseTrim(hint, 80);
            var hintMarkup = string.IsNullOrEmpty(hint) ? "" : $" — [red]{Markup.Escape(hint)}[/]";
            return $"{Gutter}│ [red]✗[/] [grey]{Markup.Escape(parts.Verb)}[/]{argMarkup}{hintMarkup}";
        }
        return $"{Gutter}│ [grey]{Markup.Escape(parts.Verb)}[/]{argMarkup}";
    }

    private static string ClassifyPhase(string? tool, string formatted)
    {
        if (tool == "file")
        {
            // FormatToolLogEntry encodes the action as the second token for file tool.
            var lower = formatted.ToLowerInvariant();
            if (lower.StartsWith("file read ") ||
                lower.StartsWith("file list ") ||
                lower.StartsWith("file glob ") ||
                lower.StartsWith("file read_tree "))
                return PhaseResearch;
            // If the formatted entry doesn't start with a known file action
            // prefix (e.g. args parsing failed and we only see "file path"),
            // default to research — most ambiguous file calls are reads,
            // lists, or globs, not destructive mutations.
            if (!(lower.StartsWith("file write ") ||
                  lower.StartsWith("file edit ") ||
                  lower.StartsWith("file delete ") ||
                  lower.StartsWith("file append ") ||
                  lower.StartsWith("file delete_dir ") ||
                  lower.StartsWith("file create_dir ") ||
                  lower.StartsWith("file move ") ||
                  lower.StartsWith("file copy ")))
                return PhaseResearch;
            return PhaseAction;
        }
        if (tool == "search" || tool == "web_read")
            return PhaseResearch;
        // terminal and unknown
        return PhaseAction;
    }

    /// <summary>
    /// Parses FormatToolLogEntry output:
    ///   "{tool} {action?} {arg} — {result}"
    /// into disjoint Verb (already localised), Arg (path/query/command/url),
    /// and the trailing result text.
    /// </summary>
    private static FormattedParts ParseFormatted(string formatted)
    {
        var sep = formatted.IndexOf(" — ", StringComparison.Ordinal);
        var left = sep >= 0 ? formatted[..sep].TrimEnd() : formatted.TrimEnd();
        var rest = sep >= 0 ? formatted[(sep + 3)..] : "";

        // Up to 3 tokens: tool name, optional action verb, then the arg.
        var tokens = left.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return new FormattedParts("действие", "", rest);

        string rawVerb;
        string arg;
        if (tokens.Length >= 3)
        {
            rawVerb = $"{tokens[0]} {tokens[1]}";
            arg = tokens[2];
        }
        else if (tokens.Length == 2)
        {
            // FormatToolLogEntry always emits "file <action>" for the file tool,
            // so the second token is the action, not the arg. Without this a
            // call like "file list" (no path) renders as the bare word "file".
            if (tokens[0] == "file")
            {
                rawVerb = $"{tokens[0]} {tokens[1]}";
                arg = "";
            }
            else
            {
                rawVerb = tokens[0];
                arg = tokens[1];
            }
        }
        else
        {
            rawVerb = tokens[0];
            arg = "";
        }

        return new FormattedParts(Localise(rawVerb), arg, rest);
    }

    private sealed record FormattedParts(string Verb, string Arg, string Rest);

    private static string Localise(string toolAction) => toolAction switch
    {
        "file read" => "Прочитало",
        "file list" => "Показало содержимое",
        "file glob" => "Поиск по маске",
        "file read_tree" => "Дерево папок",
        "file write" => "Записало",
        "file append" => "Дописало",
        "file edit" => "Изменило",
        "file delete" => "Удалило",
        "file delete_dir" => "Удалило папку",
        "file create_dir" => "Создало папку",
        "file move" => "Переместило",
        "file copy" => "Скопировало",
        "file unknown" => "Операция с файлом",
        "file" => "Файл",
        "search" => "Поиск",
        "web_read" => "Открыло",
        "terminal" => "Выполнило",
        _ => toolAction
    };

    /// <summary>
    /// Collapse newlines into spaces (LLM reasoning is one long thought, not
    /// multiple lines), then trim to <paramref name="max"/> chars with an
    /// ellipsis. Keeps the timeline single-line so the tree stays readable.
    /// </summary>
    private static string CollapseTrim(string s, int max)
    {
        var collapsed = string.Join(" ", s.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
    }
}
