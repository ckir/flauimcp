using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
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
builder.Services.AddSingleton<InteractionTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
