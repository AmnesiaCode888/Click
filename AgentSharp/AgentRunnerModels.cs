namespace AgentSharp;

public record AgentRunnerProgress(
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    string? FormattedEntry = null,
    string? Thinking = null,
    int? Step = null,
    string? Title = null,
    string? Description = null,
    string? Tool = null,
    string? Status = null,
    string? FilePreview = null,
    string? Reasoning = null,
    string? StreamingContent = null);

public record AgentRunnerResult(
    string Content,
    IReadOnlyList<string> ToolLog,
    UsageInfo? Usage = null,
    string? ReasoningContent = null);
