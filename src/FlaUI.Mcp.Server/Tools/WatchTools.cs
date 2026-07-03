// src/FlaUI.Mcp.Server/Tools/WatchTools.cs
using System.ComponentModel;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Watch;
using FlaUI.Mcp.Server.Watch;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Push+drain MCP surface for Phase-8 desktop_watch (§3/Spike B). Every tool method takes a leading
/// `McpServer server` param (SDK-injected, not part of the JSON schema — like CancellationToken) and captures it
/// into <see cref="McpEventSink"/> on every call; this is the DEFINITIVE McpServer capture (the hosted-service
/// DI-resolve attempt in WatchPumpHostedService is best-effort-only, since the SDK may not have created the
/// session McpServer yet at host start). All four tools are ReadOnly + lease-exempt (they synthesize no input).</summary>
[McpServerToolType]
public sealed class WatchTools
{
    private readonly WatchService _watch;
    private readonly McpEventSink _sink;
    public WatchTools(WatchService watch, McpEventSink sink) { _watch = watch; _sink = sink; }

    [McpServerTool(ReadOnly = true), Description(
        "Subscribe to UIA events on a window and receive them as async MCP notifications " +
        "(method 'notifications/flaui/desktop_event') instead of polling snapshots. events is one+ of: " +
        "window_opened, window_closed (child dialogs/popups of this window), focus_changed (input focus " +
        "moves within this window's process), structure_changed (subtree repopulated - coalesced/debounced). " +
        "Optional scope=<a live ref> narrows structure_changed to that element's subtree. Returns " +
        "{subscriptionId, window, events, scope?}. Each notification payload is " +
        "{subscriptionId, event, window, ref?, controlType?, name?, bounds?, coalescedCount, timestampUtc} " +
        "(name is [REDACTED] for password fields; ref/name/bounds may be absent, e.g. window_closed). The " +
        "payload 'ref' is EPHEMERAL - minted into a small bounded pool, so it returns REF_NOT_FOUND if you wait " +
        "too long to act; re-desktop_snapshot for a durable ref. ReadOnly + lease-exempt. " +
        "IMPORTANT - many hosts (including Claude Code) do NOT surface these push notifications to the model: " +
        "in those you MUST poll desktop_drain_events(subscriptionId) to actually receive the events (the server " +
        "buffers them for you) - do not just register a watch and wait for a notification that never arrives. " +
        "NOTE: your OWN " +
        "desktop_type/click/key calls fire events too - events right after your input are likely self-caused " +
        "(correlate by timing). Caps: 5 watches/window, 20/session (TooManyWatches).")]
    public Task<string> DesktopWatch(
        McpServer server,
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Event kinds: window_opened, window_closed, focus_changed, structure_changed.")] string[] events,
        [Description("Optional live ref to scope structure_changed to a subtree.")] string? scope = null,
        [Description("Registration timeout in ms (default 4000).")] int timeoutMs = 4000)
        => ToolResponse.Guard(async () =>
        {
            _sink.SetServer(server);
            if (events is null || events.Length == 0)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "events must contain at least one event kind.",
                    "pass e.g. [\"window_opened\",\"structure_changed\"]");
            var kinds = new List<WatchEventKind>();
            foreach (var t in events)
            {
                if (!WatchEventKinds.TryParse(t, out var k))
                    throw new ToolException(ToolErrorCode.InvalidArguments,
                        $"Unknown event kind '{t}'.",
                        "use window_opened, window_closed, focus_changed, or structure_changed");
                if (!kinds.Contains(k)) kinds.Add(k);
            }
            var sub = await _watch.WatchAsync(window, kinds, scope, timeoutMs);
            return ToolResponse.Ok(new { subscriptionId = sub.Id, window, events = sub.Kinds.Select(WatchEventKinds.ToWire), scope });
        });

    [McpServerTool(ReadOnly = true), Description(
        "Stop a subscription created by desktop_watch. Idempotent: an unknown/already-ended subscriptionId " +
        "returns ok:true (your intent - no more events - is satisfied).")]
    public Task<string> DesktopUnwatch(
        McpServer server,
        [Description("The subscriptionId from desktop_watch, e.g. s1.")] string subscriptionId)
        => ToolResponse.Guard(async () =>
        {
            _sink.SetServer(server);
            await _watch.UnwatchAsync(subscriptionId);
            return ToolResponse.Ok(new { ok = true, subscriptionId });
        });

    [McpServerTool(ReadOnly = true), Description(
        "List your active watch subscriptions (recover them after a context loss). Returns " +
        "watches[{subscriptionId, window, events, scope?, droppedCount}] - droppedCount>0 means some events " +
        "were coalesced-dropped under load (re-snapshot to resync).")]
    public Task<string> DesktopListWatches(McpServer server)
        => ToolResponse.Guard(() =>
        {
            _sink.SetServer(server);
            var watches = _watch.List().Select(w => new
            {
                subscriptionId = w.SubscriptionId, window = w.WindowId,
                events = w.Kinds.Select(WatchEventKinds.ToWire), scope = w.Scope, droppedCount = w.DroppedCount
            });
            return Task.FromResult(ToolResponse.Ok(new { watches }));
        });

    [McpServerTool(ReadOnly = true), Description(
        "Fetch and clear buffered events for a subscription. USE THIS in hosts that do NOT surface push " +
        "notifications (e.g. Claude Code today): desktop_watch delivers via 'notifications/flaui/desktop_event' " +
        "AND buffers each event here as a fallback. Returns {subscriptionId, events:[<same payload shape>], count}. " +
        "The buffer is bounded (oldest dropped under load). Returned droppedCount is the SUM of coalescer + buffer " +
        "evictions for this subscription (>0 means you missed some state - re-desktop_snapshot to resync). Event 'ref's " +
        "are ephemeral: a drained ref may already be REF_NOT_FOUND if you waited too long - re-desktop_snapshot for a " +
        "durable ref. Do NOT also rely on push in a host that surfaces it (you'd see each event twice).")]
    public Task<string> DesktopDrainEvents(
        McpServer server,
        [Description("The subscriptionId from desktop_watch, e.g. s1.")] string subscriptionId,
        [Description("Max events to return this call (default: all buffered).")] int? max = null)
        => ToolResponse.Guard(() =>
        {
            _sink.SetServer(server);
            var events = _watch.Drain(subscriptionId, max);
            // Surface the summed loss (coalescer + drain-buffer evictions) inline so a draining agent sees missed
            // state without a second desktop_list_watches call (AGY-AFTER merge-gate seat B, both drops share the
            // registry counter). 0 if the subscription is already evicted (window closed).
            var droppedCount = _watch.List().FirstOrDefault(w => w.SubscriptionId == subscriptionId)?.DroppedCount ?? 0;
            return Task.FromResult(ToolResponse.Ok(new { subscriptionId, events, count = events.Count, droppedCount }));
        });
}
