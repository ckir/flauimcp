// src/FlaUI.Mcp.Server/Watch/WatchPumpHostedService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Watch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Watch;

/// <summary>Hosts the Core WatchPump. StartAsync starts the pump and best-effort-captures the session McpServer
/// into McpEventSink (a DI resolve; the definitive capture is WatchTools' tool-param in Task 10). StopAsync stops
/// the pump (complete channel + stop worker) THEN disposes all held UIA registrations (WatchService.DisposeAllAsync,
/// each self-marshaling onto the query STA) — §9 teardown order: pump first, then registrations.</summary>
public sealed class WatchPumpHostedService : IHostedService
{
    private readonly WatchPump _pump;
    private readonly WatchService _service;
    private readonly McpEventSink _sink;
    private readonly IServiceProvider _sp;

    public WatchPumpHostedService(WatchPump pump, WatchService service, McpEventSink sink, IServiceProvider sp)
    { _pump = pump; _service = service; _sink = sink; _sp = sp; }

    public Task StartAsync(CancellationToken ct)
    {
        // Best-effort: capture McpServer if the SDK exposes it in DI at start (it may not be created until the
        // transport connects — that's fine, WatchTools captures it from its tool param in Task 10).
        var server = _sp.GetService<McpServer>();
        if (server is not null) _sink.SetServer(server);
        return _pump.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _pump.StopAsync(ct);          // stop worker + complete channel FIRST
        await _service.DisposeAllAsync();   // then dispose UIA registrations (self-marshal onto query STA)
    }
}
