namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Outcome of one agent's install/uninstall. <see cref="NotFound"/> means the agent isn't present
/// (normal — most users have only one); <see cref="Failed"/> means it IS present and we could not
/// configure it. The two must stay distinct: only the latter is worth telling anyone about.
/// </summary>
public enum AgentChange { Created, Updated, Unchanged, Removed, NotFound, Failed }

/// <summary>
/// Outcome of an install/uninstall against one agent's config file(s). <paramref name="Warning"/>
/// carries a non-fatal shortfall — the agent IS configured, but something optional alongside it
/// (e.g. the seed driving skill) did not land. It must be surfaced: a shortfall nobody reports is
/// how a feature goes missing without anyone noticing.
/// </summary>
public sealed record AgentResult(string Agent, AgentChange Change, string Detail, string? Warning = null);
