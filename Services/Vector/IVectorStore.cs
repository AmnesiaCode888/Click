namespace Click.Services.Vector;

public interface IVectorStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task InsertChunksAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default);
    Task DeleteFileChunksAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CodeChunk>> SearchAsync(float[] queryEmbedding, int limit, string? languageFilter = null, string? globFilter = null, CancellationToken cancellationToken = default);
    Task<int> GetChunkCountAsync(CancellationToken cancellationToken = default);
    Task<int> GetFileCountAsync(CancellationToken cancellationToken = default);
    Task<IndexStats?> GetStatsAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task UpsertFileIndexAsync(string filePath, string fileHash, long lastModified, int chunkCount, CancellationToken cancellationToken = default);
    Task<string?> GetFileHashAsync(string filePath, CancellationToken cancellationToken = default);
    Task SaveMetaAsync(string key, string value, CancellationToken cancellationToken = default);
}
