using AgentSharp;

namespace Click.Infrastructure;

public interface IAgentRegistry
{
    IAgent GetAgent(string id);
    IReadOnlyList<IAgent> GetAgents();
}

public class AgentRegistry : IAgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents;

    public AgentRegistry(IEnumerable<IAgent> agents)
    {
        _agents = agents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IAgent GetAgent(string id) =>
        _agents.TryGetValue(id, out var agent)
            ? agent
            : throw new InvalidOperationException($"Агент '{id}' не найден");

    public IReadOnlyList<IAgent> GetAgents() => _agents.Values.ToList();
}
