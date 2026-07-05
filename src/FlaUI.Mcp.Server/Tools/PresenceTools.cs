using System.ComponentModel;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Presence;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class PresenceTools
{
    private readonly ServerOptions _options;
    private readonly PresenceState _state;
    private readonly IIdleSource _idle;

    public PresenceTools(ServerOptions options, PresenceState state, IIdleSource idle)
    { _options = options; _state = state; _idle = idle; }

    [McpServerTool(ReadOnly = true), Description("Report the human's COARSE presence: { enabled, activity: \"active\"|\"nearby\"|\"away\"|null }. \"active\" = recent input; \"nearby\" = idle past a short threshold; \"away\" = idle past a longer one. Read-only, lease-EXEMPT. Off by default — returns { enabled:false, activity:null } until a human runs `flaui-mcp presence on`. NEVER returns raw idle milliseconds (privacy). Combine with desktop_focus_window/desktop_wait_for_foreground to derive watching/working and escalate how you signal the human.")]
    public Task<string> DesktopUserState()
        => ToolResponse.Guard(() =>
        {
            // Launch flag sets the default; the live state file overrides it (immediate off).
            var launchDefault = new PresenceConfig(_options.Presence, _options.NearbySecs, _options.AwaySecs);
            var cfg = _state.Read(launchDefault);
            long idleMs = cfg.Enabled ? _idle.IdleMs() : 0; // don't even read the clock when disabled
            return Task.FromResult(Reply(cfg, idleMs));
        });

    /// <summary>Pure reply builder — the unified non-polymorphic shape (spec §3.1). Disabled → activity null;
    /// enabled → the coarse enum only. Never emits raw idle-ms.</summary>
    public static string Reply(PresenceConfig cfg, long idleMs)
    {
        if (!cfg.Enabled) return ToolResponse.Ok(new { enabled = false, activity = (string?)null });
        var a = IdleActivity.Bucket(idleMs, cfg.NearbySecs * 1000L, cfg.AwaySecs * 1000L);
        var s = a switch { Activity.Active => "active", Activity.Nearby => "nearby", _ => "away" };
        return ToolResponse.Ok(new { enabled = true, activity = (string?)s });
    }
}
