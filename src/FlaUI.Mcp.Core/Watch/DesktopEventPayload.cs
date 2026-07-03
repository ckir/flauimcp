// src/FlaUI.Mcp.Core/Watch/DesktopEventPayload.cs
using System.Text.Json.Serialization;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>§4 notification wire contract for ONE emitted event. Required keys always present;
/// nullable keys (ref/controlType/name/bounds) are JsonIgnore-when-null. "event"/"ref" are JSON
/// keys that collide with C# keywords, so they carry explicit JsonPropertyName. `bounds` is
/// [x,y,w,h]. `timestampUtc` is a pre-formatted ISO-8601 Z string (built by WatchPayloadBuilder).</summary>
public sealed record DesktopEventPayload(
    [property: JsonPropertyName("subscriptionId")] string SubscriptionId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref,
    [property: JsonPropertyName("controlType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ControlType,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonPropertyName("bounds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? Bounds,
    [property: JsonPropertyName("coalescedCount")] int CoalescedCount,
    [property: JsonPropertyName("timestampUtc")] string TimestampUtc);
