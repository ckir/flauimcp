// src/FlaUI.Mcp.Core/Watch/IUiaEventSource.cs
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>What one subscription needs registered on the query STA. WindowId = the top-level window handle id;
/// WindowProcessId = its cached PID (for the §7 filter); ScopeRef = the subscribed structure_changed scope ref
/// (null = window root); CoalesceScope = the string used as the structure_changed coalesce key part (§8).</summary>
public sealed record WatchSubscriptionSpec(
    string SubscriptionId, string WindowId, int WindowProcessId,
    IReadOnlyList<WatchEventKind> Kinds, string? ScopeRef, string CoalesceScope);

/// <summary>The raw UIA-side event delivery. Register returns a disposable that unregisters EXACTLY this
/// subscription's handlers (ref-counting global registrations internally, §7). The callback fires on a COM
/// RPC thread: it MUST read only CACHED props and MUST NOT throw (§6/§16.4). Implemented by Uia3EventSource
/// on the query STA; faked in future headless tests.</summary>
public interface IUiaEventSource
{
    /// <param name="onCapture">invoked on the COM thread with the plain-data meta AND an opaque source token
    /// the payload-build later resolves on the STA (null for window_closed). MUST NOT block.</param>
    IDisposable Register(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture);
}
