using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSharp;
using Click.Infrastructure;
using Click.Services.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Click;

public static class ApiServer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static string _currentMode = "code";
    private static readonly object _modeLock = new();

    private static readonly Dictionary<string, List<ChatMessage>> _histories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = new(),
        ["question"] = new(),
        ["security"] = new()
    };

    private static readonly object _historyLock = new();

    public static async Task StartAsync(IServiceProvider outerServices, CancellationToken ct)
    {
        var port = 5077;
        var portConfig = outerServices.GetService(typeof(Microsoft.Extensions.Options.IOptions<ApiOptions>))
            as Microsoft.Extensions.Options.IOptions<ApiOptions>;
        if (portConfig?.Value?.Port is > 0)
            port = portConfig.Value.Port;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting("urls", $"http://localhost:{port}");
        builder.Services.AddCors();

        var app = builder.Build();

        app.UseCors(p => p
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        var runner = outerServices.GetRequiredService<IAgentRunner>();
        var registry = outerServices.GetRequiredService<IAgentRegistry>();
        var chatService = outerServices.GetRequiredService<IChatService>();
        var aiOptions = outerServices.GetRequiredService<OpenAiOptions>();
        var workspaceOptions = outerServices.GetRequiredService<ClickWorkspaceOptions>();
        var vectorIndex = outerServices.GetService(typeof(VectorIndexService)) as VectorIndexService;

        var workspacePath = workspaceOptions.GetResolvedBasePath();
        var metadata = new AgentMetadata(
            CurrentDateTime: DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
            OperatingSystem: Environment.OSVersion.Platform.ToString() + " " + Environment.OSVersion.VersionString,
            WorkspaceDescription: BuildWorkspaceDescription(workspacePath));

        // ── POST /api/chat/stream  (SSE) ──────────────────────────────
        app.MapPost("/api/chat/stream", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<ChatApiRequest>(JsonOpts);
            if (body == null || string.IsNullOrWhiteSpace(body.Message))
            {
                await WriteSseEvent(req.HttpContext.Response, "error", "Missing message");
                return;
            }

            var mode = NormalizeMode(body.Mode);
            var agent = registry.GetAgent(mode);
            var context = new AgentContext(workspacePath, metadata);

            List<ChatMessage> history;
            lock (_historyLock)
            {
                if (!_histories.ContainsKey(mode))
                    _histories[mode] = new();
                history = _histories[mode];
            }

            var response = req.HttpContext.Response;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";

            var progress = new ApiSseProgress(response);

            try
            {
                var result = await runner.RunAsync(
                    agent, context, body.Message, history,
                    model: body.Model ?? aiOptions.Model,
                    progress: progress,
                    cancellationToken: req.HttpContext.RequestAborted);

                if (!string.IsNullOrEmpty(result.Content))
                {
                    await WriteSseData(response, new { type = "content", text = result.Content });
                }

                var usageObj = result.Usage != null
                    ? new { promptTokens = result.Usage.PromptTokens, completionTokens = result.Usage.CompletionTokens, totalTokens = result.Usage.TotalTokens }
                    : null;

                await WriteSseData(response, new { type = "done", usage = usageObj });
                await WriteSseDone(response);

                lock (_historyLock)
                {
                    history.Add(new ChatMessage("user", body.Message));
                    history.Add(new ChatMessage("assistant", result.Content));
                    TrimHistory(history);
                }
            }
            catch (OperationCanceledException)
            {
                await WriteSseData(response, new { type = "error", text = "Cancelled" });
                await WriteSseDone(response);
            }
            catch (Exception ex)
            {
                var msg = ex.Message.Length > 300 ? ex.Message[..297] + "..." : ex.Message;
                await WriteSseData(response, new { type = "error", text = msg });
                await WriteSseDone(response);
            }
        });

        // ── POST /api/chat  (regular) ─────────────────────────────────
        app.MapPost("/api/chat", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<ChatApiRequest>(JsonOpts);
            if (body == null || string.IsNullOrWhiteSpace(body.Message))
                return Results.BadRequest(new { error = "Missing message" });

            var mode = NormalizeMode(body.Mode);
            var agent = registry.GetAgent(mode);
            var context = new AgentContext(workspacePath, metadata);

            List<ChatMessage> history;
            lock (_historyLock)
            {
                if (!_histories.ContainsKey(mode))
                    _histories[mode] = new();
                history = _histories[mode];
            }

            try
            {
                var result = await runner.RunAsync(
                    agent, context, body.Message, history,
                    model: body.Model ?? aiOptions.Model,
                    cancellationToken: req.HttpContext.RequestAborted);

                lock (_historyLock)
                {
                    history.Add(new ChatMessage("user", body.Message));
                    history.Add(new ChatMessage("assistant", result.Content));
                    TrimHistory(history);
                }

                return Results.Ok(new
                {
                    content = result.Content,
                    usage = result.Usage != null
                        ? new { promptTokens = result.Usage.PromptTokens, completionTokens = result.Usage.CompletionTokens, totalTokens = result.Usage.TotalTokens }
                        : null
                });
            }
            catch (OperationCanceledException)
            {
                return Results.BadRequest(new { error = "Cancelled" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ── GET /api/models ───────────────────────────────────────────
        app.MapGet("/api/models", async (HttpRequest req) =>
        {
            try
            {
                var models = await chatService.GetAvailableModelsAsync(
                    req.HttpContext.RequestAborted);
                return Results.Ok(new { models });
            }
            catch
            {
                return Results.Ok(new { models = Array.Empty<string>() });
            }
        });

        // ── GET /api/status ───────────────────────────────────────────
        app.MapGet("/api/status", () =>
        {
            string mode;
            lock (_modeLock) { mode = _currentMode; }
            int historyCount;
            lock (_historyLock)
            {
                historyCount = _histories.TryGetValue(mode, out var h) ? h.Count : 0;
            }
            return Results.Ok(new
            {
                mode,
                model = aiOptions.Model,
                workspace = workspacePath,
                historyMessages = historyCount,
                port
            });
        });

        // ── POST /api/mode ────────────────────────────────────────────
        app.MapPost("/api/mode", async (HttpRequest req) =>
        {
            var body = await req.ReadFromJsonAsync<ModeRequest>(JsonOpts);
            if (body == null || string.IsNullOrWhiteSpace(body.Mode))
                return Results.BadRequest(new { error = "Missing mode" });

            var mode = NormalizeMode(body.Mode);
            lock (_modeLock) { _currentMode = mode; }

            var agent = registry.GetAgent(mode);
            return Results.Ok(new { mode, agentName = agent.Name });
        });

        // ── POST /api/clear ───────────────────────────────────────────
        app.MapPost("/api/clear", () =>
        {
            lock (_historyLock)
            {
                foreach (var h in _histories.Values) h.Clear();
            }
            return Results.Ok(new { ok = true });
        });

        // ── GET /api/health ───────────────────────────────────────────
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", port }));

        Console.WriteLine($"  [dim]API: http://localhost:{port}[/]");

        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetResult());

        _ = app.RunAsync();

        await tcs.Task;
        await app.StopAsync();
    }

    // ── SSE helpers ────────────────────────────────────────────────────

    private static async Task WriteSseData(Microsoft.AspNetCore.Http.HttpResponse response, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await response.Body.WriteAsync(bytes);
        await response.Body.FlushAsync();
    }

    private static async Task WriteSseDone(Microsoft.AspNetCore.Http.HttpResponse response)
    {
        var bytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await response.Body.WriteAsync(bytes);
        await response.Body.FlushAsync();
    }

    private static async Task WriteSseEvent(Microsoft.AspNetCore.Http.HttpResponse response, string type, string text)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        var data = JsonSerializer.Serialize(new { type, text }, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await response.Body.WriteAsync(bytes);
        await response.Body.FlushAsync();
    }

    // ── Internal types ─────────────────────────────────────────────────

    public class ApiSseProgress : IProgress<AgentRunnerProgress>
    {
        private readonly Microsoft.AspNetCore.Http.HttpResponse _response;
        private readonly object _lock = new();

        public ApiSseProgress(Microsoft.AspNetCore.Http.HttpResponse response)
        {
            _response = response;
        }

        public async void Report(AgentRunnerProgress value)
        {
            try
            {
                if (!string.IsNullOrEmpty(value.StreamingContent))
                {
                    await WriteSseData(_response, new { type = "content", text = value.StreamingContent });
                }
                else if (!string.IsNullOrEmpty(value.Reasoning))
                {
                    var truncated = value.Reasoning.Length > 500 ? value.Reasoning[..497] + "..." : value.Reasoning;
                    await WriteSseData(_response, new { type = "reasoning", text = truncated });
                }
                else if (value.FormattedEntry != null)
                {
                    var statusValue = value.Status ?? "ok";
                    await WriteSseData(_response, new
                    {
                        type = "tool",
                        name = value.Tool ?? "",
                        action = value.FormattedEntry,
                        status = statusValue
                    });
                }
                else if (!string.IsNullOrEmpty(value.Title))
                {
                    await WriteSseData(_response, new { type = "progress", text = value.Title, step = value.Step });
                }
            }
            catch { /* response may be disposed */ }
        }
    }

    // ── DTOs ───────────────────────────────────────────────────────────

    public record ChatApiRequest(
        string Message,
        string? Mode = null,
        string? Model = null);

    public record ModeRequest(string Mode);

    public class ApiOptions
    {
        public const string SectionName = "Api";
        public int Port { get; set; } = 5077;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string NormalizeMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "question" or "q" => "question",
        "security" or "s" => "security",
        _ => "code"
    };

    private static void TrimHistory(List<ChatMessage> history)
    {
        const int maxMessages = 40;
        const int maxChars = 50000;

        if (history.Count > maxMessages)
            history.RemoveRange(0, history.Count - maxMessages);

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
        var sb = new StringBuilder();
        try
        {
            var csprojFiles = Directory.GetFiles(workspacePath, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                sb.AppendLine("Projects:");
                foreach (var csproj in csprojFiles)
                    sb.AppendLine($"  - {Path.GetFileName(csproj)}");
            }

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
                break;
            }

            var entries = Directory.GetFileSystemEntries(workspacePath)
                .Where(e => !Path.GetFileName(e).StartsWith("."))
                .OrderBy(e => Directory.Exists(e) ? 0 : 1)
                .ThenBy(e => Path.GetFileName(e))
                .ToList();

            if (entries.Count > 0)
            {
                sb.AppendLine($"\nStructure ({entries.Count} items):");
                foreach (var entry in entries.Take(30))
                {
                    var name = Path.GetFileName(entry);
                    sb.AppendLine($"  {(Directory.Exists(entry) ? name + "/" : name)}");
                }
                if (entries.Count > 30)
                    sb.AppendLine($"  ... and {entries.Count - 30} more");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(workspace scan error: {ex.Message})");
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "(empty directory)" : result;
    }
}
