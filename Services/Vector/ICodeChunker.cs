namespace Click.Services.Vector;

public interface ICodeChunker
{
    string[] SupportedLanguages { get; }
    IReadOnlyList<CodeChunk> ChunkFile(string filePath, string content, string fileHash);
}
