namespace AgentSharp;

public interface IToolHandler
{
    Task<string?> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default);
}
