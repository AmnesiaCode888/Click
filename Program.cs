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

var metadata = new AgentMetadata(
    CurrentDateTime: DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
    OperatingSystem: Environment.OSVersion.Platform.ToString() + " " + Environment.OSVersion.VersionString);
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
        history.Add(new ChatMessage("assistant", result.Content, ReasoningContent: result.ReasoningContent));

        const int maxHistoryMessages = 20;
        const int maxHistoryChars = 25000;
        if (history.Count > maxHistoryMessages)
            history.RemoveRange(0, history.Count - maxHistoryMessages);

        int totalChars = 0;
        foreach (var m in history) totalChars += m.Content?.Length ?? 0;
        while (totalChars > maxHistoryChars && history.Count > 2)
        {
            totalChars -= history[0].Content?.Length ?? 0;
            history.RemoveAt(0);
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
