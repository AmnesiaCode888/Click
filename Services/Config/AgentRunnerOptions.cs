namespace Click;

public class AgentRunnerOptions
{
    public const string SectionName = "Agent";

    public int MaxIterations { get; set; } = 50;
    public int MaxToolResultCharsKeep { get; set; } = 8000;
    public int MaxToolResultCharsSuccess { get; set; } = 2000;
    public int PreserveRecentToolRounds { get; set; } = 4;
}
