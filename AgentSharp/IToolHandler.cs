namespace AgentSharp;

public interface IToolHandler
{
    Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default);
}
