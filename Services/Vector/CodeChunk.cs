namespace Click.Services.Vector;

public record CodeChunk(
    string Id,
    string FilePath,
    string Language,
    string? SymbolName,
    string? SymbolType,
    string? ParentScope,
    int StartLine,
    int EndLine,
    string Content,
    string FileHash,
    float[] Embedding)
{
    public float CosineScore { get; set; }
}

public record SemanticSearchResult(
    string FilePath,
    int StartLine,
    int EndLine,
    string? SymbolName,
    string? SymbolType,
    string? ParentScope,
    string Language,
    string Content,
    float Score);
