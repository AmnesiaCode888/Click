namespace AgentSharp;

public static class ToolResultFormatter
{
    public static string Error(string prefix, string message, string? hint = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{prefix}: {message}");
        if (!string.IsNullOrEmpty(hint))
            sb.Append($". Подсказка: {hint}");
        return sb.ToString();
    }
}
