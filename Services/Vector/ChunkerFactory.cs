namespace Click.Services.Vector;

public sealed class ChunkerFactory
{
    private readonly RegexBasedChunker _regexChunker = new();
    private readonly FallbackChunker _fallbackChunker = new();

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".py", ".go",
        ".rs", ".java", ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx",
        ".php", ".rb"
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".json", ".yaml", ".yml", ".xml", ".ini", ".toml"
    };

    public ICodeChunker GetChunker(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (CodeExtensions.Contains(ext))
            return _regexChunker;
        return _fallbackChunker;
    }

    public bool IsIndexable(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return CodeExtensions.Contains(ext) || DocExtensions.Contains(ext);
    }
}
