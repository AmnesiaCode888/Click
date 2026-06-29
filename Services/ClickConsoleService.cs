using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp;
using Click.Infrastructure;
using Spectre.Console;

namespace Click.Services;

/// <summary>
/// Console UI service: validates configuration, builds a human-readable
/// workspace description, and runs the interactive ReAct chat loop.
/// Supports multiple modes (code/question/security) swappable via /mode command.
/// </summary>
public class ClickConsoleService : IClickConsoleService
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentRunner _runner;
    private readonly IChatService _chatService;
    private readonly OpenAiOptions _aiOptions;
    private readonly ClickChatOptions _chatOptions;
    private readonly ClickWorkspaceOptions _workspaceOptions;

    private enum AgentMode { Code, Question, Security }

    private AgentMode _currentMode = AgentMode.Code;

    public ClickConsoleService(
        IAgentRegistry registry,
        IAgentRunner runner,
        IChatService chatService,
        OpenAiOptions aiOptions,
        ClickChatOptions chatOptions,
        ClickWorkspaceOptions workspaceOptions)
    {
        _registry = registry;
        _runner = runner;
        _chatService = chatService;
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
        var workspaceDescription = BuildWorkspaceDescription(workspacePath);

        var metadata = new AgentMetadata(
            CurrentDateTime: DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
            OperatingSystem: Environment.OSVersion.Platform.ToString() + " " + Environment.OSVersion.VersionString,
            WorkspaceDescription: workspaceDescription);
        var context = new AgentContext(workspacePath, metadata);

        AnsiConsole.MarkupLine("[bold cyan]Click[/] — AI-ассистент для разработки");
        AnsiConsole.MarkupLine($"[dim]Модель: {_aiOptions.Model} | Директория: {workspacePath}[/]");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape("/mode [code|question|security] → смена режима | /help → команды")}[/]\n");

        var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/exit", "/quit", "/q",
            "/clear",
            "/models", "/config",
            "/security-review", "/s-r",
            "/mode", "/m"
        };

        // Per-mode history so switching modes doesn't mix contexts
        var histories = new Dictionary<AgentMode, List<ChatMessage>>
        {
            [AgentMode.Code] = new(),
            [AgentMode.Question] = new(),
            [AgentMode.Security] = new()
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            var modeTag = GetModeTag(_currentMode);
            AnsiConsole.Markup($"{modeTag} [bold green]>>>[/] ");
            string? input;
            try
            {
                input = Console.ReadLine();
            }
            catch
            {
                break; // stdin closed (Ctrl+C)
            }
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input[0] == '/')
            {
                // Allow /mode with arguments and /m prefix
                var isModeCmd = input.StartsWith("/mode ", StringComparison.OrdinalIgnoreCase)
                    || input.Equals("/mode", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("/m ", StringComparison.OrdinalIgnoreCase)
                    || input.Equals("/m", StringComparison.OrdinalIgnoreCase);

                // Just "/" or unknown/help command → show help
                if (input.Length == 1 || (!knownCommands.Contains(input) && !isModeCmd))
                {
                    ShowHelp();
                    continue;
                }
            }

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)
                || input.Equals("/quit", StringComparison.OrdinalIgnoreCase)
                || input.Equals("/q", StringComparison.OrdinalIgnoreCase))
                break;

            if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var h in histories.Values) h.Clear();
                AnsiConsole.MarkupLine("[dim]История очищена[/]\n");
                continue;
            }

            if (input.Equals("/models", StringComparison.OrdinalIgnoreCase))
            {
                await ShowModelsAsync();
                continue;
            }

            if (input.Equals("/config", StringComparison.OrdinalIgnoreCase))
            {
                await ShowConfigAsync();
                continue;
            }

            if (input.Equals("/security-review", StringComparison.OrdinalIgnoreCase)
                || input.Equals("/s-r", StringComparison.OrdinalIgnoreCase))
            {
                var securityAgent = _registry.GetAgent("security");
                var securityHistory = new List<ChatMessage>();
                await ProcessInputAsync(securityAgent, context, "Проведи security review всего workspace", securityHistory, cancellationToken);
                continue;
            }

            if (input.StartsWith("/mode", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith("/m ", StringComparison.OrdinalIgnoreCase)
                || input.Equals("/m", StringComparison.OrdinalIgnoreCase))
            {
                HandleModeCommand(input);
                continue;
            }

            try
            {
                var (agent, history) = ResolveAgentAndHistory(context, histories);
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

    private void HandleModeCommand(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            // Just "/mode" — show current mode
            var (name, desc) = GetModeInfo(_currentMode);
            AnsiConsole.MarkupLine($"[cyan]Текущий режим:[/] [bold]{name}[/] — {desc}\n");
            return;
        }

        var target = parts[1].Trim().ToLowerInvariant();
        switch (target)
        {
            case "code":
            case "c":
                SwitchMode(AgentMode.Code);
                break;
            case "question":
            case "q":
                SwitchMode(AgentMode.Question);
                break;
            case "security":
            case "s":
                SwitchMode(AgentMode.Security);
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Неизвестный режим: {target}. Доступные: code, question, security[/]\n");
                break;
        }
    }

    private void SwitchMode(AgentMode mode)
    {
        if (_currentMode == mode) return;
        _currentMode = mode;
        var (name, desc) = GetModeInfo(mode);
        AnsiConsole.MarkupLine($"[cyan]Режим переключён на:[/] [bold]{name}[/] — {desc}\n");
    }

    private (IAgent Agent, List<ChatMessage> History) ResolveAgentAndHistory(
        AgentContext context, Dictionary<AgentMode, List<ChatMessage>> histories)
    {
        var history = histories[_currentMode];
        var agentId = _currentMode switch
        {
            AgentMode.Code => "code",
            AgentMode.Question => "question",
            AgentMode.Security => "security",
            _ => "code"
        };
        return (_registry.GetAgent(agentId), history);
    }

    private static string GetModeTag(AgentMode mode) => mode switch
    {
        AgentMode.Code => "[bold cyan][[CODE]][/]",
        AgentMode.Question => "[bold magenta][[QUESTION]][/]",
        AgentMode.Security => "[bold yellow][[SECURITY]][/]",
        _ => "[bold cyan][[CODE]][/]"
    };

    private static (string Name, string Description) GetModeInfo(AgentMode mode) => mode switch
    {
        AgentMode.Code => ("CODE", "полный доступ: чтение, запись, терминал"),
        AgentMode.Question => ("QUESTION", "только чтение: консультации по коду"),
        AgentMode.Security => ("SECURITY", "только чтение: поиск уязвимостей"),
        _ => ("CODE", "полный доступ")
    };

    private bool ValidateConfiguration()
    {
        var model = _aiOptions.Model;
        var warnings = new List<string>();
        var effectiveBaseUrl = ResolveEffectiveBaseUrl();
        var effectiveApiKey = ResolveEffectiveApiKey();
        var isLocal = effectiveBaseUrl.Contains("localhost") || effectiveBaseUrl.Contains("127.0.0.1");

        if (string.IsNullOrWhiteSpace(effectiveApiKey) && !isLocal)
            warnings.Add("API-ключ не настроен.");

        if (string.IsNullOrWhiteSpace(effectiveBaseUrl))
            warnings.Add("BaseUrl не настроен.");

        if (string.IsNullOrWhiteSpace(model))
            warnings.Add("Модель не указана.");

        if (warnings.Count > 0)
        {
            AnsiConsole.WriteLine();
            var panel = new Panel(
                string.Join("\n", warnings.Select(w => $"[yellow]  ⚠ {Markup.Escape(w)}[/]")) +
                "\n\n[dim]Используйте /config для интерактивной настройки[/]")
            {
                Header = new PanelHeader(" Внимание "),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Yellow),
                Padding = new Padding(1, 1, 1, 1)
            };
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }

        return true;
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
        AnsiConsole.MarkupLine("  [white]│[/]");
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

    private async Task ShowModelsAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Загружаю список моделей…[/]");

        var baseUrl = ResolveEffectiveBaseUrl();
        var apiKey = ResolveEffectiveApiKey();

        string[] models = Array.Empty<string>();
        bool fetchFailed = false;
        try
        {
            models = await _chatService.GetAvailableModelsAsync(baseUrl, apiKey);
        }
        catch (Exception ex)
        {
            fetchFailed = true;
            AnsiConsole.MarkupLine($"[yellow]⚠ Не удалось получить список моделей: {Markup.Escape(ex.Message)}[/]");
        }

        string selectedModel;
        if (models.Length > 0)
        {
            // Group by prefix (e.g. "openai/gpt-4" → group "openai")
            var grouped = models
                .OrderBy(m => m)
                .GroupBy(m => m.Contains('/') ? m.Split('/', 2)[0] : "(без категории)")
                .OrderBy(g => g.Key)
                .ToList();

            var prompt = new SelectionPrompt<string>()
                .Title("[cyan]Выберите модель:[/]")
                .PageSize(15)
                .UseConverter(Markup.Escape)
                .MoreChoicesText("[grey](Стрелки вверх/вниз для навигации)[/]");

            foreach (var group in grouped)
            {
                var choices = group.ToList();
                if (choices.Count == 1)
                {
                    prompt.AddChoice(choices[0]);
                }
                else
                {
                    prompt.AddChoiceGroup(Markup.Escape(group.Key), choices);
                }
            }

            // Add manual input option at the end
            const string manualChoice = "→ Ввести вручную";
            prompt.AddChoice(manualChoice);

            selectedModel = AnsiConsole.Prompt(prompt);
            if (selectedModel == manualChoice)
            {
                selectedModel = AnsiConsole.Prompt(
                    new TextPrompt<string>("[cyan]Введите имя модели:[/]")
                        .DefaultValue(_aiOptions.Model));
            }
        }
        else if (!fetchFailed)
        {
            AnsiConsole.MarkupLine("[yellow]API не вернул список моделей. Введите имя вручную.[/]");
            selectedModel = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Введите имя модели:[/]")
                    .DefaultValue(_aiOptions.Model));
        }
        else
        {
            selectedModel = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Введите имя модели:[/]")
                    .DefaultValue(_aiOptions.Model));
        }

        if (!string.IsNullOrWhiteSpace(selectedModel) && selectedModel != _aiOptions.Model)
        {
            _aiOptions.Model = selectedModel;
            SaveOpenAiConfig();
            AnsiConsole.MarkupLine($"[green]✓ Модель изменена на:[/] [bold]{Markup.Escape(selectedModel)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]Модель не изменена.[/]");
        }

        AnsiConsole.WriteLine();
    }

    private async Task ShowConfigAsync()
    {
        while (true)
        {
            var apiKey = _aiOptions.ApiKey;
            var maskedKey = MaskApiKey(apiKey);
            var resolvedBaseUrl = ResolveEffectiveBaseUrl();

            AnsiConsole.WriteLine();
            var panel = new Panel(
                $"[bold cyan]Model:[/]            [green]{Markup.Escape(_aiOptions.Model)}[/]\n" +
                $"[bold cyan]BaseUrl:[/]          [dim]{Markup.Escape(resolvedBaseUrl)}[/]\n" +
                $"[bold cyan]ApiKey:[/]           {(string.IsNullOrWhiteSpace(apiKey) ? "[red](не задан)[/]" : $"[dim]{maskedKey}[/]")}\n" +
                $"[bold cyan]MaxTokens:[/]        {_aiOptions.MaxTokens}\n" +
                $"[bold cyan]Timeout:[/]          {_aiOptions.RequestTimeoutSeconds} сек\n" +
                $"[dim](настраивается интерактивно или в appsettings.json)[/]")
            {
                Header = new PanelHeader(" Конфигурация "),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Aqua),
                Padding = new Padding(1, 2, 1, 2)
            };
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Что хотите изменить?[/]")
                    .AddChoices(new[]
                    {
                        "Сменить модель",
                        "Сменить API Key",
                        "Сменить Base URL",
                        "Пресет: Ollama (локально)",
                        "Пресет: LM Studio (локально)",
                        "Пресет: OpenRouter",
                        "Назад"
                    }));

            switch (choice)
            {
                case "Сменить модель":
                    await ShowModelsAsync();
                    break;

                case "Сменить API Key":
                    var newKey = AnsiConsole.Prompt(
                        new TextPrompt<string>("[cyan]Введите API Key (оставьте пустым чтобы удалить):[/]")
                            .AllowEmpty()
                            .Secret()
                            .DefaultValue(_aiOptions.ApiKey ?? ""));
                    _aiOptions.ApiKey = string.IsNullOrWhiteSpace(newKey) ? null : newKey;
                    SaveOpenAiConfig();
                    _chatService.ClearModelCache();
                    AnsiConsole.MarkupLine("[green]✓ API Key обновлён.[/]\n");
                    break;

                case "Сменить Base URL":
                    var newUrl = AnsiConsole.Prompt(
                        new TextPrompt<string>("[cyan]Введите Base URL:[/]")
                            .DefaultValue(_aiOptions.BaseUrl));
                    _aiOptions.BaseUrl = string.IsNullOrWhiteSpace(newUrl) ? "" : newUrl;
                    SaveOpenAiConfig();
                    _chatService.ClearModelCache();
                    AnsiConsole.MarkupLine("[green]✓ Base URL обновлён.[/]\n");
                    break;

                case "Пресет: Ollama (локально)":
                    ApplyPreset("http://localhost:11434/v1", apiKey: null);
                    AnsiConsole.MarkupLine("[green]✓ Пресет Ollama применён.[/]\n");
                    await ShowModelsAsync();
                    return;

                case "Пресет: LM Studio (локально)":
                    ApplyPreset("http://localhost:1234/v1", apiKey: null);
                    AnsiConsole.MarkupLine("[green]✓ Пресет LM Studio применён.[/]\n");
                    await ShowModelsAsync();
                    return;

                case "Пресет: OpenRouter":
                    var openRouterKey = AnsiConsole.Prompt(
                        new TextPrompt<string>("[cyan]Введите API Key для OpenRouter:[/]")
                            .AllowEmpty()
                            .Secret()
                            .DefaultValue(_aiOptions.ApiKey ?? ""));
                    ApplyPreset("https://openrouter.ai/api/v1", string.IsNullOrWhiteSpace(openRouterKey) ? null : openRouterKey);
                    AnsiConsole.MarkupLine("[green]✓ Пресет OpenRouter применён.[/]\n");
                    await ShowModelsAsync();
                    return;

                case "Назад":
                    return;
            }
        }
    }

    private void ApplyPreset(string baseUrl, string? apiKey)
    {
        _aiOptions.BaseUrl = baseUrl;
        _aiOptions.ApiKey = apiKey;

        // Strip legacy prefixes so BaseUrl takes precedence over old prefix-based routing
        var model = _aiOptions.Model;
        if (model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            _aiOptions.Model = model["ollama/".Length..];
        else if (model.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase))
            _aiOptions.Model = model["lmstudio/".Length..];
        else if (model.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
            _aiOptions.Model = model["lm-studio/".Length..];

        SaveOpenAiConfig();
        _chatService.ClearModelCache();
    }

    private void SaveOpenAiConfig()
    {
        try
        {
            var path = "appsettings.Development.json";
            var json = File.Exists(path) ? File.ReadAllText(path) : "{}";
            var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

            if (!root.ContainsKey("OpenAi"))
                root["OpenAi"] = new JsonObject();

            var openAi = root["OpenAi"]!.AsObject();
            openAi["Model"] = _aiOptions.Model;
            openAi["BaseUrl"] = _aiOptions.BaseUrl;
            openAi["ApiKey"] = _aiOptions.ApiKey;

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Не удалось сохранить конфигурацию: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private string ResolveEffectiveBaseUrl()
    {
        var model = _aiOptions.Model;
        if (model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            return _aiOptions.OllamaBaseUrl ?? "http://localhost:11434/v1";
        if (model.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
            return _aiOptions.LmStudioBaseUrl ?? "http://localhost:1234/v1";
        return _aiOptions.BaseUrl;
    }

    private string? ResolveEffectiveApiKey()
    {
        var model = _aiOptions.Model;
        if (model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            return _aiOptions.OllamaApiKey;
        if (model.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
            return _aiOptions.LmStudioApiKey;
        return _aiOptions.ApiKey;
    }

    private static string MaskApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(не задан)";
        if (key.Length <= 8) return new string('*', key.Length);
        return key[..4] + new string('*', key.Length - 8) + key[^4..];
    }

    private static void ShowHelp()
    {
        AnsiConsole.WriteLine();
        var panel = new Panel(
            "[bold yellow]/exit, /quit, /q[/]        Завершить сессию\n" +
            "[bold yellow]/clear[/]                Очистить историю сообщений\n" +
            "[bold yellow]/models[/]               Выбрать модель (интерактивно)\n" +
            "[bold yellow]/config[/]               Настроить API (интерактивно)\n" +
            "[bold yellow]/mode " + Markup.Escape("[code|question|security]") + "[/]  Переключить режим\n" +
            "[bold yellow]/security-review, /s-r[/] Запустить security review (read-only)\n" +
            "[bold yellow]/help, /h, /?[/]          Показать это меню")
        {
            Header = new PanelHeader(" Доступные команды "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow),
            Padding = new Padding(1, 2, 1, 2)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
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
