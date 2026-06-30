namespace AgentSharp;

public interface IChatService
{
    Task<AgentChatResponse> ChatAsync(
        string userMessage,
        IReadOnlyList<ChatMessage>? history = null,
        IReadOnlyList<ApiTool>? tools = null,
        string? model = null,
        CancellationToken cancellationToken = default);

    Task<AgentChatResponse> ChatWithMessagesAsync(
        IReadOnlyList<ApiMessage> messages,
        IReadOnlyList<ApiTool>? tools = null,
        string? model = null,
        CancellationToken cancellationToken = default,
        IProgress<string>? contentStream = null);

    Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    Task<string[]> GetAvailableModelsAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default);

    void ClearModelCache();
}
