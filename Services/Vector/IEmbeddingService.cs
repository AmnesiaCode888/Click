namespace Click.Services.Vector;

public interface IEmbeddingService
{
    Task<float[][]> GetEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
    int Dimensions { get; }
    bool IsAvailable { get; }
}
