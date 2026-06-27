using System.Text.Json.Serialization;

namespace AgentSharp;

public record ApiMessage(
    string Role,
    string? Content = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ApiToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolCallId = null,
    [property: JsonPropertyName("reasoning_content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningContent = null
);

public record ApiToolCall(
    string Id,
    string Type,
    ApiFunctionCall Function
);

public record ApiFunctionCall(
    string Name,
    string Arguments
);

public record ApiTool(
    string Type,
    ApiFunction Function
);

public record ApiFunction(
    string Name,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Parameters = null
);

public record ChatRequest(
    string Model,
    IReadOnlyList<ApiMessage> Messages,
    [property: JsonPropertyName("max_tokens")]
    int MaxTokens,
    bool Stream,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ApiTool>? Tools = null,
    [property: JsonPropertyName("parallel_tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ParallelToolCalls = null,
    [property: JsonPropertyName("tool_choice")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? ToolChoice = null
);
