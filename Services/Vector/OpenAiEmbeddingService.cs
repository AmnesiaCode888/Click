using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Click.Services.Vector;

public sealed class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly EmbeddingOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsAvailable => true;
    public int Dimensions => _options.Dimensions > 0 ? _options.Dimensions : 768;

    public OpenAiEmbeddingService(HttpClient httpClient, EmbeddingOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<float[][]> GetEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        // Gemini/OpenRouter может не поддерживать пакетную эмбеддинг, отправляем по одному
        const int batchSize = 1;
        var allResults = new float[texts.Count][];

        for (int batchStart = 0; batchStart < texts.Count; batchStart += batchSize)
        {
            var batch = texts.Skip(batchStart).Take(batchSize).ToList();
            var batchResult = await GetBatchAsync(batch, cancellationToken);
            for (int i = 0; i < batchResult.Length; i++)
                allResults[batchStart + i] = batchResult[i];
        }

        return allResults;
    }

    private async Task<float[][]> GetBatchAsync(List<string> texts, CancellationToken cancellationToken)
    {
        var model = _options.Model ?? "text-embedding-3-small";
        var url = _options.BaseUrl!.TrimEnd('/') + "/embeddings";
        int? dimensions = _options.Dimensions > 0 ? _options.Dimensions : null;
        var request = new EmbeddingRequest(Model: model, Input: texts, Dimensions: dimensions);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = JsonContent.Create(request, options: JsonOptions);
        SetAuthHeaders(req);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        using var response = await _httpClient.SendAsync(req, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            throw new InvalidOperationException($"Embedding API error {(int)response.StatusCode}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var result = new float[texts.Count][];
        foreach (var item in data.EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32();
            var embedding = item.GetProperty("embedding");
            var vector = new float[embedding.GetArrayLength()];
            int i = 0;
            foreach (var val in embedding.EnumerateArray())
                vector[i++] = val.GetSingle();
            result[index] = vector;
        }

        return result;
    }

    private void SetAuthHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    private record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] List<string> Input,
        [property: JsonPropertyName("dimensions")] int? Dimensions = null);
}
