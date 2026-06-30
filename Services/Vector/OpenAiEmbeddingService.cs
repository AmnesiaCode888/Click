using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

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
    public string ModelName => _options.Model ?? "unknown";

    public OpenAiEmbeddingService(HttpClient httpClient, EmbeddingOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<float[][]> GetEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        const int batchSize = 20;
        var allResults = new float[texts.Count][];

        for (int offset = 0; offset < texts.Count; offset += batchSize)
        {
            var batch = texts.Skip(offset).Take(batchSize).ToList();
            float[][] batchResult;

            try
            {
                batchResult = await GetBatchWithRetryAsync(batch, cancellationToken);
            }
            catch (Exception) when (batch.Count > 1)
            {
                // Some providers don't support batched embeddings — fallback to one-by-one
                batchResult = new float[batch.Count][];
                for (int i = 0; i < batch.Count; i++)
                {
                    try
                    {
                        var single = await GetBatchWithRetryAsync(new List<string> { batch[i] }, cancellationToken);
                        batchResult[i] = single[0];
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Embedding failed for text index {offset + i}", ex);
                    }
                }
            }

            for (int i = 0; i < batch.Count && offset + i < allResults.Length; i++)
                allResults[offset + i] = batchResult[i];
        }

        return allResults;
    }

    private async Task<float[][]> GetBatchWithRetryAsync(List<string> texts, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await GetBatchAsync(texts, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                lastEx = new TimeoutException("Embedding request timed out");
                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
            }
            catch (InvalidOperationException) when (attempt < maxAttempts)
            {
                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
            }
        }

        throw lastEx ?? new InvalidOperationException("Embedding API request failed after retries");
    }

    private static TimeSpan ComputeRetryDelay(int attempt)
    {
        var jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
        var delayMs = 2000 * (int)Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(delayMs * jitter);
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
