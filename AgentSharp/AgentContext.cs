namespace AgentSharp;

public record AgentMetadata(string CurrentDateTime, string OperatingSystem);

public record AgentContext(string WorkspacePath, AgentMetadata Metadata);
