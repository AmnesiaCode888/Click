namespace Click;

public class AgentRunnerOptions
{
    public const string SectionName = "Agent";

    public int MaxIterations { get; set; } = 15;
    public int MaxToolResultCharsKeep { get; set; } = 2500;
    public int MaxToolResultCharsSuccess { get; set; } = 400;
    public int PreserveRecentToolRounds { get; set; } = 2;
}
