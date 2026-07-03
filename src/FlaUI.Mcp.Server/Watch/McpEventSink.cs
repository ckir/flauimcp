// src/FlaUI.Mcp.Server/Watch/McpEventSink.cs
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Watch;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Watch;

/// <summary>IEventSink over the MCP SDK. Push is BEST-EFFORT: Claude Code does not surface unsolicited
/// notifications (Spike B), so the reliable delivery is the drain buffer (WatchPump appends there too). If the
/// session McpServer is not yet captured, EmitAsync is a no-op — the event is still buffered for drain, so the
/// server never breaks. The McpServer is set once available (WatchTools captures it from its tool param, Task 10;
/// WatchPumpHostedService also attempts a DI resolve at start).</summary>
public sealed class McpEventSink : IEventSink
{
    private volatile McpServer? _server;
    public void SetServer(McpServer server) => _server = server;

    public async Task EmitAsync(DesktopEventPayload payload, CancellationToken ct)
    {
        var server = _server;
        if (server is null) return; // best-effort: no session server yet; drain buffer still holds the payload
        await server.SendNotificationAsync("notifications/flaui/desktop_event", payload, cancellationToken: ct);
    }
}
