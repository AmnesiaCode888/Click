namespace AgentSharp;

internal static class ToolExtensions
{
    public static ApiTool ToOpenAiTool(this Tool tool)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in tool.Parameters)
        {
            props[p.Name] = new Dictionary<string, object?>
            {
                ["type"] = p.Type,
                ["description"] = p.Description ?? ""
            };
            if (p.Enum != null && p.Enum.Length > 0)
                ((Dictionary<string, object?>)props[p.Name])["enum"] = p.Enum;
            if (p.Required)
                required.Add(p.Name);
        }

        return new ApiTool(
            Type: "function",
            Function: new ApiFunction(
                Name: tool.Name,
                Description: tool.Description,
                Parameters: new
                {
                    type = "object",
                    properties = props,
                    required = required
                }
            )
        );
    }
}
