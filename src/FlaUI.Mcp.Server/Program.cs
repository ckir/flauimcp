using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using System.Security.Principal;
using FlaUI.Mcp.Server.Install;
using FlaUI.Mcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Installer verbs run and exit; anything else (including no args) runs the MCP stdio host.
if (CliRouter.IsInstallerVerb(args))
{
    var exePath = Environment.ProcessPath
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
    return CliRouter.Run(args, exePath, Console.Out);
}

// Security: warn (on stderr — stdout is the MCP channel) if running elevated. We run the desktop at
// user integrity by design; elevation expands the blast radius of a compromised agent.
ElevationGuard.WarnIfElevated(ElevationGuard.IsElevated(), Console.Error);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(ServerOptions.FromArgs(args));

// Core singletons (one automation context for the whole server in this phase).
builder.Services.AddSingleton<AutomationDispatcher>();
builder.Services.AddSingleton<WindowManager>();
builder.Services.AddSingleton<WindowTools>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.RefRegistry>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.SnapshotCache>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.WaitCoordinator>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.PerceptionManager>();
builder.Services.AddSingleton<SnapshotTools>();
builder.Services.AddSingleton<FindTools>();
builder.Services.AddSingleton<ScreenshotTools>();
builder.Services.AddSingleton<InteractionTools>();
builder.Services.AddSingleton<ContentTools>();
builder.Services.AddSingleton<ClipboardTools>();

// --- Phase 4b synthetic-input stack (InputGuard now LIVE in DI) ---
builder.Services.AddSingleton<IPlatformEnvironment, Win32PlatformEnvironment>();
builder.Services.AddSingleton<ISyntheticInput>(sp =>
    new Win32SyntheticInput(sp.GetRequiredService<IPlatformEnvironment>()));
builder.Services.AddSingleton<ILeaseProvider, FileLeaseProvider>();
builder.Services.AddSingleton(_ => new ActionBudget());            // defaults: 60 / 60s (spec §3.4)
builder.Services.AddSingleton(_ => new InputAudit(Console.Error)); // event-only, stderr (spec §3.4)
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<ServerOptions>();
    return new InputGuard(
        sp.GetRequiredService<ISyntheticInput>(),
        sp.GetRequiredService<IPlatformEnvironment>(),
        sp.GetRequiredService<ILeaseProvider>(),
        sp.GetRequiredService<ActionBudget>(),
        sp.GetRequiredService<InputAudit>(),
        currentSid: CurrentUserSid(),
        isElevated: ElevationGuard.IsElevated(),
        allowElevation: opts.AllowElevation);
});
builder.Services.AddSingleton<InputTools>();

// --- Phase 8 desktop_watch (UIA event streaming over stdio; push+drain) ---
builder.Services.AddSingleton(_ =>
    System.Threading.Channels.Channel.CreateBounded<FlaUI.Mcp.Core.Watch.EventEnvelope>(
        new System.Threading.Channels.BoundedChannelOptions(256)
        { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite, SingleReader = true }));
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WatchDrainBuffer>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WatchRegistry>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.IUiaEventSource, FlaUI.Mcp.Core.Watch.Uia3EventSource>();
builder.Services.AddSingleton<FlaUI.Mcp.Server.Watch.McpEventSink>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.IEventSink>(sp => sp.GetRequiredService<FlaUI.Mcp.Server.Watch.McpEventSink>());
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WatchService>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WatchPump>();
builder.Services.AddHostedService<FlaUI.Mcp.Server.Watch.WatchPumpHostedService>();
builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.WatchTools>();

// --- Phase 9 accessibility wake (Prong A; null-sink held UIA registration, separate caps) ---
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WakeRegistry>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WakeService>();
builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.WakeTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

static string CurrentUserSid()
{
    // Fail-soft to "unknown" here (NOT a throw — the server must still start for perception tools);
    // an "unknown" SID is rejected by InputLease.IsValidNow (F1), so input stays locked rather than
    // mis-binding. The lease WRITER (LeaseWriter, CLI) is the side that hard-fails on an unresolved SID.
    try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value ?? "unknown"; }
    catch { return "unknown"; }
}
