using System.Globalization;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Abstracts the STA-side reads needed to build a payload from an already-resolved source element,
/// so the builder is headless-testable with a fake. The live implementation reads on the query STA and is
/// fail-soft per read. The redaction decision MUST be fail-closed (it comes from ElementContent, which
/// routes through RedactionPolicy.IsPasswordOrFailClosed).</summary>
public interface IEventSourceReader
{
    bool HasSource { get; }        // false for window_closed (no live source to read)
    Sensitivity Sensitivity { get; } // INV-5: the egress decision (OS-fail-closed in the live impl)
    string? ControlType { get; }
    string? Name { get; }          // ALREADY REDACTED by the reader (spec §7.2); the builder re-applies
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
            // The reader already returns a redacted name; re-applying the token here is DEFENSE IN DEPTH —
            // the builder cannot verify the reader honoured §7.2, and this is also what keeps INV-5
            // pinnable headlessly with a fake reader (WatchPayloadBuilderTests).
            name = reader.Sensitivity.Redact ? ElementContent.RedactedToken : reader.Name; // INV-5
        }
        return new DesktopEventPayload(
            meta.SubscriptionId, WatchEventKinds.ToWire(meta.Kind), windowId,
            @ref, controlType, name, bounds, coalescedCount,
            meta.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
    }
}
