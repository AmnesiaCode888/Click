using System.Reflection;
using System.Text;
using AgentSharp;

namespace Click.Infrastructure;

public static class PromptLoader
{
    public static string Load(string resourcePath, AgentContext context)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.{resourcePath.Replace('/', '.').Replace('\\', '.')}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Промпт не найден: {resourcePath} (искали {resourceName})");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var prompt = reader.ReadToEnd();

        return prompt
            .Replace("{{WorkspacePath}}", context.WorkspacePath)
            .Replace("{{CurrentDateTime}}", context.Metadata.CurrentDateTime)
            .Replace("{{OperatingSystem}}", context.Metadata.OperatingSystem)
            .Replace("{{WorkspaceDescription}}", context.Metadata.WorkspaceDescription ?? "(описание проекта недоступно — запустите /clear для обновления)");
    }
}
