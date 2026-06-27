using System.Reflection;

namespace AgentSharp;

public static class ToolFactory
{
    public static Tool Create<TArgs>(string name, string description)
        where TArgs : class, new()
    {
        var parameters = typeof(TArgs)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<ToolParameterAttribute>() != null)
            .Select(CreateParameter)
            .ToList();

        return new Tool(name, description, parameters);
    }

    private static ToolParameter CreateParameter(PropertyInfo property)
    {
        var attr = property.GetCustomAttribute<ToolParameterAttribute>()!;
        return new ToolParameter
        {
            Name = attr.Name ?? property.Name.ToLowerInvariant(),
            Type = attr.Type,
            Description = attr.Description,
            Required = attr.Required,
            Enum = attr.Enum
        };
    }
}
