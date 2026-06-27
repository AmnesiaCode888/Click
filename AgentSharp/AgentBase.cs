namespace AgentSharp;

public abstract class AgentBase : IAgent
{
    private readonly List<Tool> _tools = new();
    private readonly Dictionary<string, IToolHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string GetSystemPrompt(AgentContext context);

    public IReadOnlyList<Tool> GetTools() => _tools;

    public IToolHandler? GetHandler(string toolName) =>
        _handlers.TryGetValue(toolName, out var handler) ? handler : null;

    protected void AddTool<TArgs>(string name, string description, IToolHandler handler)
        where TArgs : class, new()
    {
        _tools.Add(ToolFactory.Create<TArgs>(name, description));
        _handlers[name] = handler;
    }
}
