namespace AgentSharp;

public class Tool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public IReadOnlyList<ToolParameter> Parameters { get; set; } = Array.Empty<ToolParameter>();

    public Tool() { }

    public Tool(string name, string description, IReadOnlyList<ToolParameter>? parameters = null)
    {
        Name = name;
        Description = description;
        Parameters = parameters ?? Array.Empty<ToolParameter>();
    }
}
