// src/FlaUI.Mcp.Core/Watch/CapturedEventMeta.cs
namespace FlaUI.Mcp.Core.Watch;

/// <summary>Plain-data description of one captured UIA event, produced by the COM capture thread from
/// CACHED props only (no live UIA read) — so the coalescer/payload logic stays FlaUI-free and headless.
/// The live source element (if any) travels separately in the wiring envelope, never through here.
/// SourceRuntimeId is the cached RuntimeId joined as "a,b,c" ("" if unreadable, §16.4).</summary>
public readonly record struct CapturedEventMeta(
    string SubscriptionId,
    WatchEventKind Kind,
    int SourceProcessId,
    string SourceRuntimeId,
    System.DateTime TimestampUtc)
{
    /// <summary>Kind-dependent coalesce key (§8). structure_changed collapses to the subscription's
    /// subscribedScope (passed in — known at subscribe time, no source read), NOT the per-child source
    /// RuntimeId (which is unique per changed child and would defeat collapse). All other kinds key on
    /// the cached source RuntimeId (they are not storms).</summary>
    public string CoalesceKey(string subscribedScope) => Kind == WatchEventKind.StructureChanged
        ? $"{SubscriptionId}|structure_changed|{subscribedScope}"
        : $"{SubscriptionId}|{WatchEventKinds.ToWire(Kind)}|{SourceRuntimeId}";
}
