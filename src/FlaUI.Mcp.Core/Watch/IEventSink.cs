// src/FlaUI.Mcp.Core/Watch/IEventSink.cs
using System.Threading;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Emit one built payload to the client. Server implements this over IMcpServer.SendNotificationAsync
/// (keeps WatchPump SDK-free). An emit failure signals the session is gone (pump stops cleanly).</summary>
public interface IEventSink
{
    Task EmitAsync(DesktopEventPayload payload, CancellationToken ct);
}

/// <summary>Channel item: the plain meta + the opaque source token (a FlaUI AutomationElement or null),
/// resolved to properties only on the query STA during payload-build. CoalesceScope is the structure_changed
/// key part (§8) carried from the subscription.</summary>
public readonly record struct EventEnvelope(CapturedEventMeta Meta, object? Source, string CoalesceScope);
