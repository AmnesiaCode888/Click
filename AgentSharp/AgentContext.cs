namespace AgentSharp;

public record AgentMetadata(string CurrentDateTime, string OperatingSystem, string? WorkspaceDescription = null);

public record AgentContext(string WorkspacePath, AgentMetadata Metadata);
