namespace AgentSharp;

public interface IAgentRunner
{
    Task<AgentRunnerResult> RunAsync(
        IAgent agent,
        AgentContext context,
        string userMessage,
        IReadOnlyList<ChatMessage>? history = null,
        string? model = null,
        IProgress<AgentRunnerProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
