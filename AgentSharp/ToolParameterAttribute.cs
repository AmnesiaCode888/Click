namespace AgentSharp;

[AttributeUsage(AttributeTargets.Property)]
public class ToolParameterAttribute : Attribute
{
    public string? Name { get; set; }
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
    public bool Required { get; set; }
    public string[]? Enum { get; set; }
}
