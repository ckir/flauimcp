namespace FlaUI.Mcp.Server.Install;

public enum AgentChange { Created, Updated, Unchanged, Removed, NotFound }

/// <summary>Outcome of an install/uninstall against one agent's config file(s).</summary>
public sealed record AgentResult(string Agent, AgentChange Change, string Detail);
