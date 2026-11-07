// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using System.Security.Principal;
using FlaUI.Mcp.Server.Install;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

// Installer verbs run and exit; anything else (including no args) runs the MCP stdio host.
if (CliRouter.IsInstallerVerb(args))
{
    // Environment.ProcessPath is single-file-publish-safe (Assembly.Location returns "" in a single-file
    // app — IL3000) and is populated for any normal launch, which every installer-verb invocation is;
    // MainModule.FileName is an equally single-file-safe backstop.
    var exePath = Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? AppContext.BaseDirectory;
    return CliRouter.Run(args, exePath, Console.Out);
}

// Security: warn (on stderr — stdout is the MCP channel) if running elevated. We run the desktop at
// user integrity by design; elevation expands the blast radius of a compromised agent.
ElevationGuard.WarnIfElevated(ElevationGuard.IsElevated(), Console.Error);

// Redaction rules: ABSENT flag => feature off, server starts normally. Flag PRESENT but the file is
// missing/malformed => refuse to start. An operator who authored a rule file is depending on a shield;
// starting with an empty rule set silently voids protection they asked for.
var startupOptions = ServerOptions.FromArgs(args);
var classifier = FlaUI.Mcp.Core.Perception.SensitivityClassifier.OsOnly;
string? rulesSha = null;
if (startupOptions.RedactionRules is not null)
{
    try
    {
        // ONE read: the bytes hashed are the bytes parsed. Reading twice (parse, then hash) lets the file
        // change in between, and this hash is what check-redaction-rules compares to decide whether the
        // running server is enforcing the file on disk.
        byte[] ruleBytes = File.ReadAllBytes(startupOptions.RedactionRules);
        rulesSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ruleBytes));
        classifier = FlaUI.Mcp.Core.Perception.SensitivityClassifier.ForRules(
            FlaUI.Mcp.Core.Perception.RedactionRuleFile.Parse(ruleBytes, startupOptions.RedactionRules));
    }
    catch (FlaUI.Mcp.Core.Perception.RedactionConfigException ex)
    {
        // The agent cannot read this: a server that refused to start has no tool surface. It is for the
        // HUMAN, out of band. Name both exits so recovery needs no guesswork.
        var msg = $"flaui-mcp: refusing to start — {ex.Message}\n" +
                  "Fix the named rule, or remove --redaction-rules to start without rules.";
        Console.Error.WriteLine(msg);
        // This must reach a durable log, NOT stderr alone: an MCP child that exits takes its whole tool
        // surface with it, and the operator may never open stderr.
        FlaUI.Mcp.Server.Install.ServerStateFile.TryLogStartupError(msg);
        return 2;
    }
    catch (Exception ex)
    {
        // The file could not be READ AT ALL — locked by the operator's editor, permissions, or deleted
        // between launch and open. Without this catch the exception escapes, the server dies, and it
        // takes the durable log with it: the one record the operator has to diagnose from.
        //
        // Deliberately a DIFFERENT message from the config branch above. Telling someone to "fix the
        // named rule" when the real fault is a sharing violation sends them to edit a file that is
        // perfectly valid — a recovery string must name a cause the reader can actually act on.
        var msg = $"flaui-mcp: refusing to start — could not read the redaction rule file " +
                  $"'{startupOptions.RedactionRules}': {ex.Message}\n" +
                  "Check the path and that no other process holds the file open, or remove " +
                  "--redaction-rules to start without rules.";
        Console.Error.WriteLine(msg);
        FlaUI.Mcp.Server.Install.ServerStateFile.TryLogStartupError(msg);
        return 2;
    }
}

// One file per instance; prune only positively-dead neighbours while we are here. A default-path
// operator never runs the CLI, so nothing else would ever collect orphans.
var stateDir = FlaUI.Mcp.Server.Install.ServerStateFile.DefaultDirectory;
var self = System.Diagnostics.Process.GetCurrentProcess();
FlaUI.Mcp.Server.Install.ServerStateFile.PruneDead(stateDir);
FlaUI.Mcp.Server.Install.ServerStateFile.Write(stateDir, self.Id, self.StartTime.ToUniversalTime(),
                                               startupOptions.RedactionRules, rulesSha);
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    FlaUI.Mcp.Server.Install.ServerStateFile.Delete(stateDir, self.Id);

// Structured log (docs/superpowers/specs/2026-11-07-structured-logging-design.md). ON by default; --no-log
// turns it off. A log that cannot be opened is reported and the server starts without it (spec §7).
var hasher = new ContentHasher();
bool IsSensitiveProcess(string? p) =>
    FlaUI.Mcp.Core.Perception.PerceptionPolicy.IsDenied(p) || classifier.CouldMatchProcess(p);
JsonlFileLoggerProvider? fileLog = null;
if (!startupOptions.NoLog)
{
    var logOptions = new JsonlLogOptions(JsonlLogOptions.DefaultDirectory(), self.Id,
        self.StartTime.ToUniversalTime(), startupOptions.LogLevel);
    JsonlFileLoggerProvider.PruneDead(logOptions.Directory, keepDeadFiles: 20, logOptions.IsAlive);
    fileLog = JsonlFileLoggerProvider.TryCreate(logOptions, new LogMasker(hasher, IsSensitiveProcess), out var logError);
    if (logError is not null)
    {
        Console.Error.WriteLine(logError);
        FlaUI.Mcp.Server.Install.ServerStateFile.TryLogStartupError(logError);
    }
}

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio: stdout is the JSON-RPC channel — a single framework log line on stdout corrupts the
// protocol stream, and a strict client (e.g. Antigravity/agy) then refuses to load the server. Route
// ALL host/framework logs to stderr. (InputAudit/ElevationGuard already write their output to stderr.)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
// The file provider needs Debug events, so the global floor is Trace; the console keeps its old Information
// floor, and our own structured events reach stderr only at Warning+ (plan "Spec adjustments" 4).
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Information);
builder.Logging.AddFilter<ConsoleLoggerProvider>("FlaUI.Mcp", LogLevel.Warning);
if (fileLog is not null) builder.Logging.AddProvider(fileLog);

builder.Services.AddSingleton(startupOptions);
builder.Services.AddSingleton(classifier);

// v0.10.1 intent overlay: the real GDI renderer only when --overlay is on AND the delay is non-zero;
// otherwise the zero-cost NullActionOverlay. Registered as a singleton so container disposal tears down
// the STA thread + GDI handles (SEAT-F).
builder.Services.AddSingleton<FlaUI.Mcp.Core.Interaction.IActionOverlay>(sp =>
{
    var o = sp.GetRequiredService<ServerOptions>();
    return (o.Overlay && o.OverlayMs > 0)
        ? new FlaUI.Mcp.Server.Overlay.GdiActionOverlay(o.OverlayMs)
        : FlaUI.Mcp.Core.Interaction.NullActionOverlay.Instance;
});

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
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.IWindowImageSource,
                              FlaUI.Mcp.Server.Capture.PrintWindowImageSource>();
builder.Services.AddSingleton(_ => FlaUI.Mcp.Core.Perception.CaptureCircuitBreaker.Default);
// ⚠⚠ THE COMPOSITION LIVES IN `ForPerception`, NOT HERE, AND THAT IS THE POINT. This registration used
// to build the coordinator inline, including two OPTIONAL delegates the plan calls load-bearing --
// `denylistedVisible` (omitting it is what made round 5's denylist fix INERT in production while every
// test still passed) and `desktopMasks`. Task 20 then broke five Desktop-test call sites that each would
// have had to repeat that wiring by hand, which is five more chances to forget one. The factory closes
// the seam over `PerceptionManager` in a single place that production and those tests share.
builder.Services.AddSingleton(sp =>
    FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator.ForPerception(
        sp.GetRequiredService<FlaUI.Mcp.Core.Perception.PerceptionManager>(),
        sp.GetRequiredService<FlaUI.Mcp.Core.Perception.IWindowImageSource>(),
        sp.GetRequiredService<FlaUI.Mcp.Core.Perception.CaptureCircuitBreaker>(),
        logger: sp.GetService<ILoggerFactory>()?.CreateLogger<FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator>()));
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
        allowElevation: opts.AllowElevation,
        logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<InputGuard>>());
});
// SP-A attention signals: flash is always available; TTS only when --autosound is on. Composite fans out.
builder.Services.AddSingleton(_ =>
    new FlaUI.Mcp.Core.Attention.TtsDebounce(capacity: 3, window: System.TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<FlaUI.Mcp.Core.Attention.IAttentionSignal>(sp =>
{
    var o = sp.GetRequiredService<ServerOptions>();
    var wm = sp.GetRequiredService<WindowManager>();
    var channels = new System.Collections.Generic.List<FlaUI.Mcp.Core.Attention.IAttentionSignal>
        { new FlaUI.Mcp.Server.Attention.FlashSignal(wm) };
    if (o.Autosound)
        channels.Add(new FlaUI.Mcp.Server.Attention.TtsSignal(
            h => wm.TryGetAppName(h),
            sp.GetRequiredService<FlaUI.Mcp.Core.Attention.TtsDebounce>()));
    return new FlaUI.Mcp.Core.Attention.CompositeAttentionSignal(channels);
});
builder.Services.AddSingleton(sp => new FlaUI.Mcp.Server.Capture.CaptureAuditSignal(
    sp.GetRequiredService<FlaUI.Mcp.Core.Attention.IAttentionSignal>(),
    sp.GetRequiredService<ServerOptions>().CaptureAuditSignal));
builder.Services.AddSingleton<InputTools>();
// --- ROADMAP B2: system tray ---
builder.Services.AddSingleton(sp => new FlaUI.Mcp.Core.Tray.TrayManager(
    sp.GetRequiredService<WindowManager>(),
    sp.GetRequiredService<AutomationDispatcher>(),
    sp.GetRequiredService<InputGuard>(),
    sp.GetRequiredService<IPlatformEnvironment>(),
    sp.GetRequiredService<FlaUI.Mcp.Core.Perception.SensitivityClassifier>()));
builder.Services.AddSingleton<TrayTools>();

// --- structured log: tool-call filter (only when the file log is on) ---
if (fileLog is not null)
{
    builder.Services.AddSingleton(hasher);
    builder.Services.AddSingleton(_ => ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly));
    builder.Services.AddSingleton<ICallSensitivity, CallSensitivity>();
    builder.Services.AddSingleton(sp => new ToolCallLogFilter(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("FlaUI.Mcp.Server.ToolCalls"),
        sp.GetRequiredService<ToolParameterCatalog>(), sp.GetRequiredService<ICallSensitivity>(), hasher));
}

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

// --- Phase 9 OCR text targeting (Prong B) ---
builder.Services.AddSingleton<FlaUI.Mcp.Core.Vision.IOcrEngine, FlaUI.Mcp.Core.Vision.WindowsMediaOcrEngine>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Vision.TextFinder>();

// --- SP-A T8: desktop_wait_for_foreground blocking resume primitive ---
builder.Services.AddSingleton<FlaUI.Mcp.Core.Attention.IForegroundWaiter, FlaUI.Mcp.Server.Attention.Win32ForegroundWaiter>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Attention.WaitForForeground.WaiterGate>();

builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.FindTextTools>();

// --- SP-B user-state presence (coarse, opt-in, read-only) ---
builder.Services.AddSingleton<FlaUI.Mcp.Core.Presence.IIdleSource, FlaUI.Mcp.Core.Presence.Win32IdleSource>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Presence.PresenceState>();
builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.PresenceTools>();

builder.Services
    // The install-time approval decision travels over the MCP handshake, not only in the Claude Code
    // SessionStart hook. The hook is absent between install and client relaunch, and absent silently if
    // it fails; `initialize` happens on every connect.
    //
    // Routed through a NAMED METHOD rather than an inline lambda ON PURPOSE: a method group can be called
    // directly by a test, so WHAT gets configured is proven by a real assertion instead of by grepping
    // this file. See McpServerConfiguration.
    .AddMcpServer(FlaUI.Mcp.Server.Install.McpServerConfiguration.Apply)
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(f => f.AddCallToolFilter(next => async (ctx, ct) =>
        ctx.Services?.GetService<ToolCallLogFilter>() is { } log
            ? await log.RunAsync(ctx.Params?.Name ?? "", ctx.Params?.Arguments, () => next(ctx, ct))
            : await next(ctx, ct)));

var app = builder.Build();
if (fileLog is not null)
{
    var loggers = app.Services.GetRequiredService<ILoggerFactory>();
    var serverLog = loggers.CreateLogger("FlaUI.Mcp.Server");
    ToolLog.Configure(loggers.CreateLogger("FlaUI.Mcp.Server.Tools"),
        () => LogContext.Current is { } s && fileLog.CurrentFilePath is { } path ? $"{s.Cid} in {path}" : null);
    FlaUI.Mcp.Core.Logging.LogEvent.Emit(serverLog, LogLevel.Information, "server.start",
        FlaUI.Mcp.Core.Logging.LogEvent.F("version", typeof(ServerOptions).Assembly.GetName().Version?.ToString()),
        FlaUI.Mcp.Core.Logging.LogEvent.F("flags", HostFacts.FlagNames(args)),
        FlaUI.Mcp.Core.Logging.LogEvent.F("rulesSha", rulesSha),
        FlaUI.Mcp.Core.Logging.LogEvent.F("dpiAwareness", HostFacts.DpiAwareness()),
        FlaUI.Mcp.Core.Logging.LogEvent.F("dpiScale", HostFacts.DpiScale()),
        FlaUI.Mcp.Core.Logging.LogEvent.F("session", HostFacts.Session()),
        FlaUI.Mcp.Core.Logging.LogEvent.F("readOnly", startupOptions.ReadOnly));
    var started = System.Diagnostics.Stopwatch.StartNew();
    app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        FlaUI.Mcp.Core.Logging.LogEvent.Emit(serverLog, LogLevel.Information, "server.stop",
            FlaUI.Mcp.Core.Logging.LogEvent.F("uptimeMs", started.ElapsedMilliseconds),
            FlaUI.Mcp.Core.Logging.LogEvent.F("calls", app.Services.GetService<ToolCallLogFilter>()?.Calls ?? 0),
            FlaUI.Mcp.Core.Logging.LogEvent.F("droppedTotal", fileLog.DroppedTotal)));
}
await app.RunAsync();   // disposes the host, which disposes fileLog: drains for up to 1 s (spec §7)
return 0;

static string CurrentUserSid()
{
    // Fail-soft to "unknown" here (NOT a throw — the server must still start for perception tools);
    // an "unknown" SID is rejected by InputLease.IsValidNow (F1), so input stays locked rather than
    // mis-binding. The lease WRITER (LeaseWriter, CLI) is the side that hard-fails on an unresolved SID.
    try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value ?? "unknown"; }
    catch { return "unknown"; }
}
