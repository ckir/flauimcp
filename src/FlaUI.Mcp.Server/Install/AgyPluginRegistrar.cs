namespace FlaUI.Mcp.Server.Install;

/// Registers flaui-mcp with agy via `agy plugin install "<stagingDir>"` (agy copies the dir into its
/// managed plugins dir itself). Idempotent: uninstall-then-install. Absent agy => NotFound (skip+report).
public sealed class AgyPluginRegistrar
{
    private const string Agy = "agy";
    private readonly CliInvoker _cli;

    public AgyPluginRegistrar(CliInvoker cli) => _cli = cli;

    public AgentResult Register(string stagingDir)
    {
        if (!_cli.IsPresent(Agy))
            return new AgentResult("agy", AgentChange.NotFound, "agy CLI not found — skipped");

        _cli.Invoke(Agy, "plugin", "uninstall", PluginIds.PluginName); // swallow (absent on fresh installs)
        var r = _cli.Invoke(Agy, "plugin", "install", stagingDir);
        return r.Code == 0
            ? new AgentResult("agy", AgentChange.Created, $"agy plugin install {PluginIds.PluginName}")
            : new AgentResult("agy", AgentChange.Failed, $"agy plugin install failed (exit {r.Code}): {r.Output}");
    }

    public AgentResult Unregister()
    {
        if (!_cli.IsPresent(Agy))
            return new AgentResult("agy", AgentChange.NotFound, "agy CLI not found — skipped");
        var r = _cli.Invoke(Agy, "plugin", "uninstall", PluginIds.PluginName);
        return r.Code == 0
            ? new AgentResult("agy", AgentChange.Removed, "agy plugin uninstall")
            : new AgentResult("agy", AgentChange.Failed, $"agy plugin uninstall failed (exit {r.Code}): {r.Output}");
    }
}
