namespace AgentSharp;

public interface IAgent
{
    string Id { get; }
    string Name { get; }
    string GetSystemPrompt(AgentContext context);
    IReadOnlyList<Tool> GetTools();
    IToolHandler? GetHandler(string toolName);
}
