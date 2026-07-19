using System;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Tri-state read-back for the `status` verb — distinguishes "can't even check" (no `claude` on
/// PATH) from the two checkable outcomes. This is the canonical signal for whether the Claude
/// driving skill is deployed post-installer-rework: it ships as a plugin now, not a copied skill
/// dir, so status must ask the plugin registry, not probe a retired path.
/// </summary>
public enum ClaudePluginStatus { CliNotFound, Active, NotRegistered }

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

    /// <summary>Read-back only, no mutation — the `status` verb's entry point into the same oracle
    /// <see cref="Register"/> applies post-install (present AND not Disabled/Error). Returns
    /// <see cref="ClaudePluginStatus.CliNotFound"/> rather than guessing when `claude` isn't on
    /// PATH, since `plugin list` cannot mean anything without it.</summary>
    public ClaudePluginStatus ReadStatus()
    {
        if (!_cli.IsPresent(Claude)) return ClaudePluginStatus.CliNotFound;
        var list = _cli.Invoke(Claude, "plugin", "list");
        return list.Code == 0 && IsActive(list.Output) ? ClaudePluginStatus.Active : ClaudePluginStatus.NotRegistered;
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
