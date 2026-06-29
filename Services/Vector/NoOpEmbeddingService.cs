namespace Click.Services.Vector;

/// <summary>
/// Placeholder used when the embedding service is not configured.
/// Returns empty arrays and reports IsAvailable = false so callers can skip vector operations.
/// </summary>
public sealed class NoOpEmbeddingService : IEmbeddingService
{
    public bool IsAvailable => false;
    public int Dimensions => 0;

    public Task<float[][]> GetEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Array.Empty<float[]>());
    }
}
