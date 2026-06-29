using System.Text.Json.Serialization;
using AgentSharp;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

/// <summary>
/// Read-only wrapper over <see cref="FileToolHandler"/>.
/// Exposes only read/list/glob/read_tree actions so that security-review agents
/// cannot accidentally (or intentionally) mutate the workspace.
/// </summary>
public class ReadOnlyFileToolHandler : IToolHandler
{
    private readonly FileToolHandler _inner;

    public ReadOnlyFileToolHandler(string workspacePath, FileToolOptions options, ILogger<ReadOnlyFileToolHandler> logger)
    {
        // Reuse the full handler but block all mutating actions at execution time.
        var innerLogger = new LoggerSubstitute(logger);
        _inner = new FileToolHandler(workspacePath, options, innerLogger, allowWrite: false);
    }

    public string Name => "file";

    public string Description => "Только чтение файлов проекта (read/list/glob/read_tree). Запись, удаление и изменение файлов запрещены.";

    public Type ArgsType => typeof(FileReadArgs);

    public Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
        => _inner.ExecuteAsync(argumentsJson, cancellationToken);

    /// <summary>
    /// Routes log calls from the inner <see cref="FileToolHandler"/> to the real
    /// <see cref="ILogger{ReadOnlyFileToolHandler}"/> so DI consumers get proper logging.
    /// </summary>
    private sealed class LoggerSubstitute : ILogger<FileToolHandler>
    {
        private readonly ILogger<ReadOnlyFileToolHandler> _target;

        public LoggerSubstitute(ILogger<ReadOnlyFileToolHandler> target) => _target = target;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => _target.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _target.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _target.Log(logLevel, eventId, state, exception, formatter);
    }
}

public record FileReadArgs
{
    [JsonPropertyName("action")]
    [ToolParameter(Type = "string", Description = "Только чтение: read | list | glob | read_tree", Required = true, Enum = new[] { "read", "list", "glob", "read_tree" })]
    public string? Action { get; init; }

    [JsonPropertyName("path")]
    [ToolParameter(Type = "string", Description = "Относительный путь к файлу/папке")]
    public string? Path { get; init; }

    [JsonPropertyName("offset")]
    [ToolParameter(Type = "number", Description = "Для read: начальная строка (1-based)")]
    public int? Offset { get; init; }

    [JsonPropertyName("limit")]
    [ToolParameter(Type = "number", Description = "Для read: макс. строк (по умолчанию 250)")]
    public int? Limit { get; init; }

    [JsonPropertyName("pattern")]
    [ToolParameter(Type = "string", Description = "Для glob: маска файлов (напр. **/*.cs, *.json)")]
    public string? Pattern { get; init; }

    [JsonPropertyName("max_depth")]
    [ToolParameter(Type = "number", Description = "Для read_tree: максимальная глубина (по умолчанию 3, макс 10)")]
    public int? MaxDepth { get; init; }
}
