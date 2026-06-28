namespace AgentSharp;

public record ToolResult(object? Data, string FormattedContent)
{
    public static ToolResult FromString(string content) => new(null, content);

    public static ToolResult Structured(object data, string formattedContent) => new(data, formattedContent);
}
