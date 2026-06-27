namespace AgentSharp;

public record ChatMessage(
    string Role,
    string Content,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    string? ToolCallId = null,
    string? ReasoningContent = null);

public record UsageInfo(int PromptTokens, int CompletionTokens, int TotalTokens);

public record AgentChatResponse(string Content, IReadOnlyList<ToolCallRequest> ToolCalls, UsageInfo? Usage = null, string? ReasoningContent = null);

public record ToolCallRequest(string Id, string Name, string ArgumentsJson);

public record ToolCallLog(DateTimeOffset At, string Name, string ArgumentsJson, string? Result = null);

public static class ChatMessageExtensions
{
    public static IEnumerable<ApiMessage> ToApiMessages(this IReadOnlyList<ChatMessage> history)
    {
        foreach (var m in history)
        {
            if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                yield return new ApiMessage(m.Role, m.Content,
                    ToolCalls: m.ToolCalls.Select(tc => new ApiToolCall(tc.Id, "function", new ApiFunctionCall(tc.Name, tc.ArgumentsJson))).ToList(),
                    ReasoningContent: m.ReasoningContent);
            }
            else if (!string.IsNullOrEmpty(m.ToolCallId))
            {
                yield return new ApiMessage(m.Role, m.Content ?? "", ToolCallId: m.ToolCallId, ReasoningContent: m.ReasoningContent);
            }
            else
            {
                yield return new ApiMessage(m.Role, m.Content ?? "", ReasoningContent: m.ReasoningContent);
            }
        }
    }
}
