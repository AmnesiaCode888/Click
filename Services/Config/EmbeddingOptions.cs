namespace Click;

public class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>Base URL for the embedding API (OpenAI-compatible).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>API key for the embedding service.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model name, e.g. "vertex/google/gemini-embedding-2-preview" or "text-embedding-3-small".</summary>
    public string? Model { get; set; }

    /// <summary>Output dimensions (e.g. 768 for Gemini, 1536 for text-embedding-3-small).</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>Whether the embedding service is fully configured and ready to use.</summary>
    /// <summary>
    /// Whether the embedding service is configured enough to attempt connections.
    /// ApiKey is optional (e.g. Ollama localhost doesn't need one).
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Model);
}
