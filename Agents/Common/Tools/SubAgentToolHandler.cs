using System.Text.Json.Serialization;
using AgentSharp;
using Click;
using Click.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

/// <summary>
/// Tool handler that lets an agent call a sub-agent (e.g. CodeAssistant asks QuestionAgent).
/// Runs the sub-agent in a fresh context (no shared history) with a timeout and returns the answer.
/// </summary>
public class SubAgentToolHandler : IToolHandler
{
    private readonly IAgentRunner _runner;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubAgentToolHandler> _logger;
    private readonly ClickWorkspaceOptions _workspaceOptions;
    private readonly ConversationHistoryProvider _conversationHistory;

    public SubAgentToolHandler(
        IAgentRunner runner,
        IServiceProvider serviceProvider,
        ILogger<SubAgentToolHandler> logger,
        ClickWorkspaceOptions workspaceOptions,
        ConversationHistoryProvider conversationHistory)
    {
        _runner = runner;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _workspaceOptions = workspaceOptions;
        _conversationHistory = conversationHistory;
    }

    public string Name => "ask_agent";

    public string Description =>
        "Задать вопрос другому агенту. Используй для консультации с QuestionAgent по вопросам кода. " +
        "Укажи agent_id (question) и передай точный вопрос. Ответ вернётся текстом.";

    public Type ArgsType => typeof(AskAgentArgs);

    public async Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<AskAgentArgs>(argumentsJson)
                ?? throw new ArgumentException("Не удалось десериализовать аргументы");

            var agentId = args.AgentId?.Trim();
            if (string.IsNullOrEmpty(agentId))
                return ToolResult.FromString("Ошибка: укажи agent_id (например, question)");

            var question = args.Question?.Trim();
            if (string.IsNullOrEmpty(question))
                return ToolResult.FromString("Ошибка: укажи вопрос (question)");

            // Prepend parent agent's recent context so sub-agent knows what's already been done
            var parentContext = _conversationHistory.RecentContext;
            var effectiveQuestion = string.IsNullOrEmpty(parentContext)
                ? question
                : $"{parentContext}\n\n[Вопрос от родительского агента]\n{question}";

            // Validate agent exists (resolve lazily to avoid circular dependency at construction)
            var registry = _serviceProvider.GetRequiredService<IAgentRegistry>();
            IAgent subAgent;
            try
            {
                subAgent = registry.GetAgent(agentId);
            }
            catch (InvalidOperationException)
            {
                return ToolResult.FromString($"Ошибка: агент '{agentId}' не найден. Доступные: question, security.");
            }

            // Run sub-agent with actual workspace context (no history from parent)
            var workspacePath = _workspaceOptions.GetResolvedBasePath();
            var metadata = new AgentMetadata(
                CurrentDateTime: DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                OperatingSystem: Environment.OSVersion.Platform.ToString() + " " + Environment.OSVersion.VersionString,
                WorkspaceDescription: null);
            var context = new AgentContext(workspacePath, metadata);

            _logger.LogInformation("Sub-agent '{AgentId}' called with question: {Question}", agentId, question);

            // Timeout sub-agent execution to prevent hanging
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            var result = await _runner.RunAsync(
                subAgent,
                context,
                effectiveQuestion,
                history: null,
                model: null,
                progress: null,
                cancellationToken: linkedCts.Token);

            return ToolResult.FromString(result.Content);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.FromString("Операция отменена.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubAgent tool error");
            return ToolResult.FromString($"Ошибка при вызове субагента: {ex.Message}");
        }
    }
}

public record AskAgentArgs
{
    [JsonPropertyName("agent_id")]
    [ToolParameter(Type = "string", Description = "ID агента для вызова: question (вопросы по коду)", Required = true)]
    public string? AgentId { get; init; }

    [JsonPropertyName("question")]
    [ToolParameter(Type = "string", Description = "Вопрос, который нужно задать агенту", Required = true)]
    public string? Question { get; init; }
}
