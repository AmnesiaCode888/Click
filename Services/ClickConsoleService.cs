using System.Diagnostics;
using AgentSharp;
using Click.Infrastructure;
using Spectre.Console;

namespace Click.Services;

/// <summary>
/// Console UI service: validates configuration, builds a human-readable
/// workspace description, and runs the interactive ReAct chat loop.
///
/// Takes all dependencies through constructor injection so Program.cs
/// stays a minimal composition root (config + DI + launch).
/// </summary>
public class ClickConsoleService : IClickConsoleService
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentRunner _runner;
    private readonly OpenAiOptions _aiOptions;
    private readonly ClickChatOptions _chatOptions;
    private readonly ClickWorkspaceOptions _workspaceOptions;

    public ClickConsoleService(
        IAgentRegistry registry,
        IAgentRunner runner,
        OpenAiOptions aiOptions,
        ClickChatOptions chatOptions,
        ClickWorkspaceOptions workspaceOptions)
    {
        _registry = registry;
        _runner = runner;
        _aiOptions = aiOptions;
        _chatOptions = chatOptions;
        _workspaceOptions = workspaceOptions;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!ValidateConfiguration())
            return;

        if (string.IsNullOrWhiteSpace(_workspaceOptions.BasePath))
        {
            AnsiConsole.MarkupLine("[red]Ошибка: рабочая директория не задана.[/]");
            return;
        }

        var workspacePath = _workspaceOptions.BasePath;
        var agent = _registry.GetAgent("code");
        var workspaceDescription = BuildWorkspaceDescription(workspacePath);

        var metadata = new AgentMetadata(
            CurrentDateTime: DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
            OperatingSystem: Environment.OSVersion.Platform.ToString() + " " + Environment.OSVersion.VersionString,
            WorkspaceDescription: workspaceDescription);
        var context = new AgentContext(workspacePath, metadata);

        AnsiConsole.MarkupLine($"[bold cyan]{agent.Name}[/] — AI-ассистент для разработки");
        AnsiConsole.MarkupLine($"[dim]Модель: {_aiOptions.Model} | Директория: {workspacePath}[/]\n");

        var history = new List<ChatMessage>();

        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Markup("[bold green]>>>[/] ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
            if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                history.Clear();
                AnsiConsole.MarkupLine("[dim]История очищена[/]\n");
                continue;
            }

            try
            {
                await ProcessInputAsync(agent, context, input, history, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Ошибка: {Markup.Escape(ex.Message)}[/]\n");
            }
        }
    }

    private bool ValidateConfiguration()
    {
        var model = _aiOptions.Model;
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(_aiOptions.ApiKey)
            && !model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
            errors.Add("API-ключ не настроен. Укажите OpenAi:ApiKey в appsettings.json или используйте локальную модель (ollama/lmstudio).");

        if (string.IsNullOrWhiteSpace(_aiOptions.BaseUrl)
            && !model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
            errors.Add("BaseUrl не настроен. Укажите OpenAi:BaseUrl в appsettings.json.");

        if (string.IsNullOrWhiteSpace(model))
            errors.Add("Модель не указана. Укажите OpenAi:Model в appsettings.json.");

        if (errors.Count == 0) return true;

        AnsiConsole.MarkupLine("[red]⚠ Ошибки конфигурации:[/]");
        foreach (var err in errors)
            AnsiConsole.MarkupLine($"[red]  ✗ {Markup.Escape(err)}[/]");
        AnsiConsole.MarkupLine("\n[dim]Проверьте appsettings.json или appsettings.Development.json[/]");
        return false;
    }

    private async Task ProcessInputAsync(
        IAgent agent,
        AgentContext context,
        string input,
        List<ChatMessage> history,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var observer = new RunObserver();
        AnsiConsole.MarkupLine("[dim]Запускаю агент…[/]");
        var result = await _runner.RunAsync(agent, context, input, history,
            model: _aiOptions.Model, progress: observer, cancellationToken: ct);
        sw.Stop();

        // Reasoning is streamed by RunObserver; skip the post-hoc re-print.

        // Close the phase tree with a white connector bar (user-requested:
        // the last "stick" before the answer should be white, not grey),
        // then the agent's final answer in green.
        AnsiConsole.MarkupLine("  │");
        AnsiConsole.MarkupLine($"  [bold green]● {Markup.Escape(agent.Name)}[/]");
        AnsiConsole.WriteLine();
        // Markup.Escape guards against LLM output containing brackets that
        // Spectre.Console would otherwise treat as colour tokens (e.g. an
        // explanation that *literally* writes `[bold]` would otherwise turn
        // red).
        AnsiConsole.MarkupLine(Markup.Escape(result.Content));

        var usage = result.Usage;
        var stats = usage != null
            ? $"[dim]⚡ {usage.PromptTokens} prompt + {usage.CompletionTokens} completion = {usage.TotalTokens} ток | ⏱ {sw.Elapsed.TotalSeconds:F1}c[/]"
            : $"[dim]⏱ {sw.Elapsed.TotalSeconds:F1}c[/]";
        AnsiConsole.MarkupLine(stats);

        history.Add(new ChatMessage("user", input));
        history.Add(new ChatMessage("assistant", result.Content));

        TrimHistory(history);
    }

    private void TrimHistory(List<ChatMessage> history)
    {
        var maxMessages = _chatOptions.MaxHistoryMessages > 0 ? _chatOptions.MaxHistoryMessages : 20;
        var maxChars = _chatOptions.MaxHistoryChars > 0 ? _chatOptions.MaxHistoryChars : 25000;

        if (history.Count > maxMessages)
            history.RemoveRange(0, history.Count - maxMessages);

        // Remove pairs to avoid orphaned user/assistant messages
        int totalChars = 0;
        foreach (var m in history) totalChars += m.Content?.Length ?? 0;
        while (totalChars > maxChars && history.Count >= 4)
        {
            totalChars -= (history[0].Content?.Length ?? 0) + (history[1].Content?.Length ?? 0);
            history.RemoveRange(0, 2);
        }
    }

    private static string BuildWorkspaceDescription(string workspacePath)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            // Find .csproj files
            var csprojFiles = Directory.GetFiles(workspacePath, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                sb.AppendLine("Проекты:");
                foreach (var csproj in csprojFiles)
                {
                    var name = Path.GetFileName(csproj);
                    sb.AppendLine($"  - {name}");
                }
            }

            // Find README
            var readmeFiles = Directory.GetFiles(workspacePath, "README*", SearchOption.TopDirectoryOnly);
            foreach (var readme in readmeFiles)
            {
                try
                {
                    var content = File.ReadAllText(readme);
                    var firstLines = string.Join("\n", content.Split('\n').Take(15));
                    if (firstLines.Length > 600) firstLines = firstLines[..600] + "...";
                    sb.AppendLine($"\nREADME ({Path.GetFileName(readme)}):");
                    sb.AppendLine(firstLines);
                }
                catch { }
                break; // Only read first README
            }

            // Top-level structure
            var entries = Directory.GetFileSystemEntries(workspacePath)
                .Where(e => !Path.GetFileName(e).StartsWith("."))
                .OrderBy(e => Directory.Exists(e) ? 0 : 1)
                .ThenBy(e => Path.GetFileName(e))
                .ToList();

            if (entries.Count > 0)
            {
                sb.AppendLine($"\nСтруктура корня ({entries.Count} элементов):");
                foreach (var entry in entries.Take(30))
                {
                    var name = Path.GetFileName(entry);
                    sb.AppendLine($"  {(Directory.Exists(entry) ? name + "/" : name)}");
                }
                if (entries.Count > 30)
                    sb.AppendLine($"  ... и ещё {entries.Count - 30} элементов");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(ошибка сканирования workspace: {ex.Message})");
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "(пустая директория)" : result;
    }
}
