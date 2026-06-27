namespace AgentSharp;

public class Agent
{
    public string Id { get; }
    public string Name { get; set; }
    private readonly List<Tool> _tools = new();

    public Agent(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public Agent AddTool(Tool tool)
    {
        _tools.Add(tool);
        return this;
    }

    public Agent AddTool(string name, string description, IReadOnlyList<ToolParameter>? parameters = null)
    {
        _tools.Add(new Tool(name, description, parameters));
        return this;
    }

    public IReadOnlyList<Tool> GetTools() => _tools;

    public Task<AgentChatResponse> SendRequestAsync(
        IChatService chatService,
        string userMessage,
        IReadOnlyList<ChatMessage>? history = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var toolsForApi = GetTools().Select(t => t.ToOpenAiTool()).ToList();
        return chatService.ChatAsync(
            userMessage,
            history,
            tools: toolsForApi.Count > 0 ? toolsForApi : null,
            model: model,
            cancellationToken: cancellationToken);
    }
}
