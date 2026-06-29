using AgentSharp;
using Click;
using Click.Infrastructure;
using Click.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// ── Composition root ───────────────────────────────────────────────────

static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    var openAiSection = configuration.GetSection(OpenAiOptions.SectionName);
    services.Configure<OpenAiOptions>(openAiSection);
    services.AddSingleton(openAiSection.Get<OpenAiOptions>() ?? new OpenAiOptions());

    var serperSection = configuration.GetSection("Serper");
    services.AddSingleton(new SerperOptions { ApiKey = serperSection["ApiKey"] });

    services.Configure<ClickWorkspaceOptions>(configuration.GetSection(ClickWorkspaceOptions.SectionName));
    services.AddSingleton(configuration.GetSection(ClickWorkspaceOptions.SectionName).Get<ClickWorkspaceOptions>() ?? new ClickWorkspaceOptions());

    services.AddHttpClient<OpenAiChatService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));
    services.AddHttpClient();

    services.AddSingleton<IChatService, OpenAiChatService>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenAiChatService));
        var opt = sp.GetRequiredService<OpenAiOptions>();
        return new OpenAiChatService(http, opt);
    });

    services.AddClickAgents(configuration);
    services.AddSingleton<IClickConsoleService, ClickConsoleService>();
}

// ── Bootstrap ──────────────────────────────────────────────────────────

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
    // Закрываем stdin, чтобы Console.ReadLine() не завис навсегда
    try { Console.OpenStandardInput().Close(); } catch { }
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

var consoleService = serviceProvider.GetRequiredService<IClickConsoleService>();
await consoleService.RunAsync(cts.Token);

// ── Pre-DI workspace selection ─────────────────────────────────────────
// Must run before the DI container is built because the workspace path
// is injected into IConfiguration as ClickWorkspaceOptions.BasePath.

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

        string path;
        try
        {
            path = Path.GetFullPath(input);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Некорректный путь: {ex.Message}[/]");
            continue;
        }

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
