using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AgentSharp;

namespace Click.Infrastructure;

public static class PromptLoader
{
    private static readonly Regex TemplateRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static string Load(string resourcePath, AgentContext context)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.{resourcePath.Replace('/', '.').Replace('\\', '.')}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Промпт не найден: {resourcePath} (искали {resourceName})");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var prompt = reader.ReadToEnd();

        return TemplateRegex.Replace(prompt, match =>
        {
            var key = match.Groups[1].Value;
            var value = key switch
            {
                "WorkspacePath" => context.WorkspacePath,
                "CurrentDateTime" => context.Metadata.CurrentDateTime,
                "OperatingSystem" => context.Metadata.OperatingSystem,
                "WorkspaceDescription" => context.Metadata.WorkspaceDescription ?? "(описание проекта недоступно — запустите /clear для обновления)",
                _ => match.Value
            };
            // Escape curly braces in values to prevent format corruption
            return value.Replace("{", "{{").Replace("}", "}}");
        });
    }
}
