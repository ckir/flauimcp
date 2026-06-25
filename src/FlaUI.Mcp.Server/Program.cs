using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Core singletons (one automation context for the whole server in this phase).
builder.Services.AddSingleton<AutomationDispatcher>();
builder.Services.AddSingleton<WindowManager>();
builder.Services.AddSingleton<WindowTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
