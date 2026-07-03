using System.Globalization;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Abstracts the STA-side reads needed to build a payload from an already-resolved source element,
/// so the builder is headless-testable with a fake. The live implementation (Task 8) reads on the query STA
/// and is fail-soft per read. IsPassword MUST be fail-closed (RedactionPolicy.IsPasswordOrFailClosed).</summary>
public interface IEventSourceReader
{
    bool HasSource { get; }        // false for window_closed (no live source to read)
    bool IsPassword { get; }       // INV-5 (fail-closed in the live impl)
    string? ControlType { get; }
    string? Name { get; }          // RAW; the builder redacts when IsPassword
    int[]? Bounds { get; }         // [x,y,w,h] or null (element may be gone by delivery)
    string? MintRef();             // mint an event ref (bounded layer, §16.5) or null
}

/// <summary>§4 payload assembly + §10 INV-5 redaction. Pure given a reader. The CALLER (WatchPump, Task 8)
/// decides which element the reader wraps per event kind: the event SOURCE for focus/window_opened, but the
/// subscribed SCOPE for structure_changed (§8/R5 — a coalesced structure event refs the scope/container, not
/// the transient child). So MintRef() here just mints whatever the reader points at — the scope-vs-source
/// choice is upstream, not in this builder.</summary>
public static class WatchPayloadBuilder
{
    public static DesktopEventPayload Build(CapturedEventMeta meta, string windowId, int coalescedCount, IEventSourceReader reader)
    {
        string? @ref = null, controlType = null, name = null;
        int[]? bounds = null;
        if (reader.HasSource)
        {
            @ref = reader.MintRef();
            controlType = reader.ControlType;
            bounds = reader.Bounds;
            name = reader.IsPassword ? "[REDACTED]" : reader.Name; // INV-5
        }
        return new DesktopEventPayload(
            meta.SubscriptionId, WatchEventKinds.ToWire(meta.Kind), windowId,
            @ref, controlType, name, bounds, coalescedCount,
            meta.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
    }
}
