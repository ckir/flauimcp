using System;

namespace FlaUI.Mcp.Server.Install;

/// Registers flaui-mcp with Claude Code as a local marketplace. Idempotent remove-then-add, then a
/// read-back that requires the plugin to be present AND active (a bare substring "present" is
/// false-GREEN if it listed but failed to load). Absent claude => NotFound (skip+report).
public sealed class ClaudePluginRegistrar
{
    private const string Claude = "claude";
    private readonly CliInvoker _cli;

    public ClaudePluginRegistrar(CliInvoker cli) => _cli = cli;

    public AgentResult Register(string stagingDir)
    {
        if (!_cli.IsPresent(Claude))
            return new AgentResult("claude", AgentChange.NotFound, "claude CLI not found — skipped");

        _cli.Invoke(Claude, "plugin", "marketplace", "remove", PluginIds.MarketplaceName);          // swallow
        var add = _cli.Invoke(Claude, "plugin", "marketplace", "add", stagingDir, "--scope", "user");
        if (add.Code != 0)
            return new AgentResult("claude", AgentChange.Failed, $"marketplace add failed (exit {add.Code}): {add.Output}");

        _cli.Invoke(Claude, "plugin", "uninstall", PluginIds.PluginName);                            // swallow
        var install = _cli.Invoke(Claude, "plugin", "install", PluginIds.InstallTarget, "--scope", "user");
        if (install.Code != 0)
            return new AgentResult("claude", AgentChange.Failed, $"plugin install failed (exit {install.Code}): {install.Output}");

        var list = _cli.Invoke(Claude, "plugin", "list");
        if (list.Code != 0 || !IsActive(list.Output))
            return new AgentResult("claude", AgentChange.Failed,
                $"read-back FAILED: {PluginIds.InstallTarget} not active after install: {list.Output}");

        return new AgentResult("claude", AgentChange.Created, $"installed {PluginIds.InstallTarget}");
    }

    public AgentResult Unregister()
    {
        if (!_cli.IsPresent(Claude))
            return new AgentResult("claude", AgentChange.NotFound, "claude CLI not found — skipped");
        _cli.Invoke(Claude, "plugin", "uninstall", PluginIds.PluginName);                            // swallow
        var mkt = _cli.Invoke(Claude, "plugin", "marketplace", "remove", PluginIds.MarketplaceName);
        return mkt.Code == 0
            ? new AgentResult("claude", AgentChange.Removed, "claude plugin + marketplace removed")
            : new AgentResult("claude", AgentChange.Failed, $"marketplace remove failed (exit {mkt.Code}): {mkt.Output}");
    }

    /// The plugin's line must be present AND not in a Disabled/Error state. If `plugin list` output
    /// surfaces no state token, presence alone is accepted (matches clavity's proven substring oracle).
    private static bool IsActive(string listOutput)
    {
        foreach (var line in listOutput.Split('\n'))
        {
            if (line.IndexOf(PluginIds.InstallTarget, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (line.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }
        return false;
    }
}
