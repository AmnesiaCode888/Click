namespace Click;

public class OpenAiOptions
{
    public const string SectionName = "OpenAi";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-4o-mini";
    public int MaxTokens { get; set; } = 8192;
    public int RequestTimeoutSeconds { get; set; } = 60;
    public bool UseParallelToolCalls { get; set; } = true;
    public bool UseRequiredToolChoice { get; set; } = false;
    public Dictionary<string, string>? AdditionalHeaders { get; set; }
    public string? LmStudioBaseUrl { get; set; } = "http://localhost:1234/v1";
    public string? LmStudioApiKey { get; set; }
    public string? OllamaBaseUrl { get; set; } = "http://localhost:11434/v1";
    public string? OllamaApiKey { get; set; }
}
