using System.Diagnostics;
using AgentSharp;
using Click;
using Click.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    var openAiSection = configuration.GetSection(OpenAiOptions.SectionName);
    services.Configure<OpenAiOptions>(openAiSection);
    services.AddSingleton(openAiSection.Get<OpenAiOptions>() ?? new OpenAiOptions());

    var serperSection = configuration.GetSection("Serper");
    services.AddSingleton(new SerperOptions { ApiKey = serperSection["ApiKey"] });

    services.Configure<ClickWorkspaceOptions>(configuration.GetSection(ClickWorkspaceOptions.SectionName));
    services.AddSingleton(configuration.GetSection(ClickWorkspaceOptions.SectionName).Get<ClickWorkspaceOptions>() ?? new ClickWorkspaceOptions());

    services.AddHttpClient<OpenAiChatService>();
    services.AddHttpClient();

    services.AddSingleton<IChatService, OpenAiChatService>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenAiChatService));
        var opt = sp.GetRequiredService<OpenAiOptions>();
        return new OpenAiChatService(http, opt);
    });

    services.AddClickAgents(configuration);
}

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

var workspacePath = SelectWorkspace();
if (workspacePath == null) return;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[red]Завершение работы...[/]");
};

var configWithWorkspace = new ConfigurationBuilder()
    .AddConfiguration(config)
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        { $"{ClickWorkspaceOptions.SectionName}:{nameof(ClickWorkspaceOptions.BasePath)}", workspacePath }
    })
    .Build();

var services = new ServiceCollection();
ConfigureServices(services, configWithWorkspace);
var serviceProvider = services.BuildServiceProvider();

var registry = serviceProvider.GetRequiredService<IAgentRegistry>();
var agent = registry.GetAgent("code");
var runner = serviceProvider.GetRequiredService<IAgentRunner>();
var options = serviceProvider.GetRequiredService<OpenAiOptions>();
var chatOptions = serviceProvider.GetRequiredService<ClickChatOptions>();

// Validate configuration
var configErrors = new List<string>();
if (string.IsNullOrWhiteSpace(options.ApiKey) && !options.Model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) && !options.Model.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase) && !options.Model.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
    configErrors.Add("API-ключ не настроен. Укажите OpenAi:ApiKey в appsettings.json или используйте локальную модель (ollama/lmstudio).");
if (string.IsNullOrWhiteSpace(options.BaseUrl) && !options.Model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) && !options.Model.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase) && !options.Model.StartsWith("lm-studio/", StringComparison.OrdinalIgnoreCase))
    configErrors.Add("BaseUrl не настроен. Укажите OpenAi:BaseUrl в appsettings.json.");
if (string.IsNullOrWhiteSpace(options.Model))
    configErrors.Add("Модель не указана. Укажите OpenAi:Model в appsettings.json.");

if (configErrors.Count > 0)
{
    AnsiConsole.MarkupLine("[red]⚠ Ошибки конфигурации:[/]");
    foreach (var err in configErrors)
        AnsiConsole.MarkupLine($"[red]  ✗ {Markup.Escape(err)}[/]");
    AnsiConsole.MarkupLine("\n[dim]Проверьте appsettings.json или appsettings.Development.json[/]");
    return;
}

var workspaceDescription = BuildWorkspaceDescription(workspacePath);

var metadata = new AgentMetadata(
    CurrentDateTime: DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
    OperatingSystem: Environment.OSVersion.Platform.ToString() + " " + Environment.OSVersion.VersionString,
    WorkspaceDescription: workspaceDescription);
var context = new AgentContext(workspacePath, metadata);

AnsiConsole.MarkupLine($"[bold cyan]{agent.Name}[/] — AI-ассистент для разработки");
AnsiConsole.MarkupLine($"[dim]Модель: {options.Model} | Директория: {workspacePath}[/]\n");

var history = new List<ChatMessage>();

while (!cts.Token.IsCancellationRequested)
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
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(agent, context, input, history, model: options.Model, cancellationToken: cts.Token);
        sw.Stop();

        if (!string.IsNullOrEmpty(result.ReasoningContent))
        {
            AnsiConsole.MarkupLine($"[dim]🤔 {Markup.Escape(result.ReasoningContent)}[/]\n");
        }

        AnsiConsole.MarkupLine($"[bold cyan]{agent.Name}:[/] {Markup.Escape(result.Content)}");

        var usage = result.Usage;
        var stats = usage != null
            ? $"[dim]⚡ {usage.PromptTokens} prompt + {usage.CompletionTokens} completion = {usage.TotalTokens} ток | ⏱ {sw.Elapsed.TotalSeconds:F1}c[/]"
            : $"[dim]⏱ {sw.Elapsed.TotalSeconds:F1}c[/]";
        AnsiConsole.MarkupLine(stats);

        history.Add(new ChatMessage("user", input));
        history.Add(new ChatMessage("assistant", result.Content));

        var maxHistoryMessages = chatOptions.MaxHistoryMessages > 0 ? chatOptions.MaxHistoryMessages : 20;
        var maxHistoryChars = chatOptions.MaxHistoryChars > 0 ? chatOptions.MaxHistoryChars : 25000;
        if (history.Count > maxHistoryMessages)
            history.RemoveRange(0, history.Count - maxHistoryMessages);

        // Remove pairs to avoid orphaned user/assistant messages
        int totalChars = 0;
        foreach (var m in history) totalChars += m.Content?.Length ?? 0;
        while (totalChars > maxHistoryChars && history.Count >= 4)
        {
            totalChars -= (history[0].Content?.Length ?? 0) + (history[1].Content?.Length ?? 0);
            history.RemoveRange(0, 2);
        }
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

static string? SelectWorkspace()
{
    var currentDir = Directory.GetCurrentDirectory();
    AnsiConsole.MarkupLine($"\n[bold cyan]Рабочая директория[/]");
    AnsiConsole.MarkupLine($"[dim]Текущая: {currentDir}[/]\n");

    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[cyan]Выберите действие:[/]")
            .AddChoices(new[]
            {
                "✓ Использовать текущую директорию",
                "📂 Указать другую директорию",
                "❌ Отмена"
            }));

    return choice switch
    {
        "✓ Использовать текущую директорию" => currentDir,
        "📂 Указать другую директорию" => PromptForCustomPath(),
        _ => null
    };
}

static string BuildWorkspaceDescription(string workspacePath)
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

static string? PromptForCustomPath()
{
    while (true)
    {
        AnsiConsole.MarkupLine("\n[dim]Введите путь к директории (или 'cancel' для отмены):[/]");
        AnsiConsole.Markup("[yellow]>[/] ");
        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = Path.GetFullPath(input);

        if (!Directory.Exists(path))
        {
            var create = AnsiConsole.Confirm($"[yellow]Директория '{path}' не существует. Создать?[/]", defaultValue: true);
            if (create)
            {
                try
                {
                    Directory.CreateDirectory(path);
                    AnsiConsole.MarkupLine($"[green]✓ Директория создана: {path}[/]");
                    return path;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Ошибка: {ex.Message}[/]");
                    continue;
                }
            }
            continue;
        }

        AnsiConsole.MarkupLine($"[green]✓ Выбрана директория: {path}[/]");
        return path;
    }
}
