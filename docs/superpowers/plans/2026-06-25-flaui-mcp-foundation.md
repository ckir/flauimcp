# FlaUI.Mcp — Phase 1: Foundation & Window Management — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the solution, the STA automation core, and window-management tools so a runnable MCP server can enumerate, open, launch, focus, and close Windows desktop windows over stdio.

**Architecture:** Layered .NET solution. `FlaUI.Mcp.Core` holds the automation engine (zero MCP deps) — all UIA/COM work is marshaled onto dedicated STA threads via `AutomationDispatcher` (a split **query** + **action** context so a blocking `InvokePattern.Invoke` can never freeze reads). `FlaUI.Mcp.Server` is the MCP host (stdio in this phase) exposing thin tool adapters. A bundled WPF `FlaUI.Mcp.TestApp` with known `AutomationId`s is the deterministic test target. xUnit covers Core.

**Tech Stack:** `net10.0-windows`, C#; `FlaUI.UIA3`; official `ModelContextProtocol` C# SDK; xUnit; WPF (TestApp).

**Phase boundary:** This plan ships a working stdio MCP server with five window tools. Snapshots, interaction, vision, and HTTP transport are Phases 2–6 (separate plans, gated on this one merging). Do NOT implement them here.

**Reference spec:** `docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md`

---

## File Structure

```
aidesktop/
├─ FlaUI.Mcp.sln
├─ src/
│  ├─ FlaUI.Mcp.Core/
│  │  ├─ FlaUI.Mcp.Core.csproj
│  │  ├─ Threading/StaThreadContext.cs        # one dedicated STA thread + work queue
│  │  ├─ Threading/AutomationDispatcher.cs     # query + action STA contexts; timeout abandonment
│  │  ├─ Errors/ToolErrorCode.cs               # error-code enum
│  │  ├─ Errors/ToolException.cs               # carries code + suggestedRecovery
│  │  ├─ Windows/WindowHandle.cs               # w# handle + descriptor
│  │  ├─ Windows/WindowManager.cs              # discovery, registry, Process.Exited invalidation
│  │  └─ Session/SessionManager.cs             # handle/process tracking, lifecycle + kill policy
│  └─ FlaUI.Mcp.Server/
│     ├─ FlaUI.Mcp.Server.csproj
│     ├─ Program.cs                            # host + stdio transport + DPI awareness
│     ├─ app.manifest                          # Per-Monitor-V2 DPI
│     └─ Tools/WindowTools.cs                  # MCP adapters over WindowManager/SessionManager
├─ test/
│  ├─ FlaUI.Mcp.Tests/
│  │  ├─ FlaUI.Mcp.Tests.csproj
│  │  ├─ Threading/StaThreadContextTests.cs
│  │  ├─ Threading/AutomationDispatcherTests.cs
│  │  ├─ Errors/ToolExceptionTests.cs
│  │  ├─ Windows/WindowManagerTests.cs
│  │  ├─ Windows/WindowOperationsTests.cs
│  │  ├─ Session/SessionManagerTests.cs
│  │  ├─ Server/WindowToolsTests.cs
│  │  └─ TestAppFixture.cs                     # launches/kills TestApp for tests
│  └─ FlaUI.Mcp.TestApp/
│     ├─ FlaUI.Mcp.TestApp.csproj
│     ├─ App.xaml / App.xaml.cs
│     └─ MainWindow.xaml / MainWindow.xaml.cs  # controls with known AutomationIds
```

**Responsibilities:** threading is isolated in `Threading/` (the riskiest code, tested hardest). Windows vs Session are split: `WindowManager` resolves/tracks live windows; `SessionManager` owns per-connection lifecycle + kill policy. Tools are thin — no UIA logic in `Server`.

---

## Task 0: Solution & project scaffolding

**Files:**
- Create: `FlaUI.Mcp.sln`, the four `.csproj` files, `src/FlaUI.Mcp.Server/app.manifest`

- [ ] **Step 1: Create solution and projects**

Run:
```bash
cd "C:/Users/user/Development/c#/aidesktop"
dotnet new sln -n FlaUI.Mcp
dotnet new classlib -n FlaUI.Mcp.Core   -o src/FlaUI.Mcp.Core   -f net10.0-windows
dotnet new console  -n FlaUI.Mcp.Server -o src/FlaUI.Mcp.Server -f net10.0-windows
dotnet new xunit    -n FlaUI.Mcp.Tests  -o test/FlaUI.Mcp.Tests  -f net10.0-windows
dotnet new wpf      -n FlaUI.Mcp.TestApp -o test/FlaUI.Mcp.TestApp -f net10.0-windows
dotnet sln add src/FlaUI.Mcp.Core src/FlaUI.Mcp.Server test/FlaUI.Mcp.Tests test/FlaUI.Mcp.TestApp
dotnet add src/FlaUI.Mcp.Server reference src/FlaUI.Mcp.Core
dotnet add test/FlaUI.Mcp.Tests reference src/FlaUI.Mcp.Core src/FlaUI.Mcp.Server
```

- [ ] **Step 2: Add NuGet packages**

Run:
```bash
dotnet add src/FlaUI.Mcp.Core   package FlaUI.UIA3
dotnet add src/FlaUI.Mcp.Server package ModelContextProtocol
dotnet add src/FlaUI.Mcp.Server package Microsoft.Extensions.Hosting
```
Note: `ModelContextProtocol` is a preview SDK — if `dotnet add` reports no stable version, append `--prerelease`. After restore, confirm the installed version and that `AddMcpServer`, `WithStdioServerTransport`, `WithToolsFromAssembly`, `[McpServerToolType]`, `[McpServerTool]` exist (Task 9 verifies by compiling). If an API name differs in your version, adapt the host wiring only — keep tool signatures unchanged.

- [ ] **Step 3: Delete template stub files**

Delete `src/FlaUI.Mcp.Core/Class1.cs` and any `UnitTest1.cs` template if present.

- [ ] **Step 4: Set Core/Server csproj properties**

In `src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj` and `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`, inside the existing `<PropertyGroup>` add:
```xml
<Nullable>enable</Nullable>
<LangVersion>latest</LangVersion>
<ImplicitUsings>enable</ImplicitUsings>
```

- [ ] **Step 5: Add DPI manifest to Server**

Create `src/FlaUI.Mcp.Server/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```
Then in `FlaUI.Mcp.Server.csproj` `<PropertyGroup>` add: `<ApplicationManifest>app.manifest</ApplicationManifest>`.

- [ ] **Step 6: Build to verify scaffold**

Run: `dotnet build`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "chore: scaffold FlaUI.Mcp solution (Core/Server/Tests/TestApp), net10.0-windows"
```

---

## Task 1: `StaThreadContext` — a dedicated STA worker thread

**Files:**
- Create: `src/FlaUI.Mcp.Core/Threading/StaThreadContext.cs`
- Test: `test/FlaUI.Mcp.Tests/Threading/StaThreadContextTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Threading;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Threading;

public class StaThreadContextTests
{
    [Fact]
    public async Task Runs_work_on_an_STA_thread()
    {
        using var ctx = new StaThreadContext("test-sta");
        var state = await ctx.RunAsync(() => Thread.CurrentThread.GetApartmentState());
        Assert.Equal(ApartmentState.STA, state);
    }

    [Fact]
    public async Task Marshals_all_work_to_the_same_single_thread()
    {
        using var ctx = new StaThreadContext("test-sta");
        var id1 = await ctx.RunAsync(() => Environment.CurrentManagedThreadId);
        var id2 = await ctx.RunAsync(() => Environment.CurrentManagedThreadId);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task Propagates_exceptions_to_the_caller()
    {
        using var ctx = new StaThreadContext("test-sta");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.RunAsync<int>(() => throw new InvalidOperationException("boom")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~StaThreadContextTests`
Expected: FAIL — `StaThreadContext` does not exist (compile error).

- [ ] **Step 3: Implement `StaThreadContext`**

```csharp
using System.Collections.Concurrent;

namespace FlaUI.Mcp.Core.Threading;

/// <summary>A single dedicated STA thread that serially executes queued work.</summary>
public sealed class StaThreadContext : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaThreadContext(string name)
    {
        _thread = new Thread(Run) { Name = name, IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
            work();
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~StaThreadContextTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Threading/StaThreadContext.cs test/FlaUI.Mcp.Tests/Threading/StaThreadContextTests.cs
git commit -m "feat(core): StaThreadContext - dedicated STA worker thread"
```

---

## Task 2: Error envelope — `ToolErrorCode` + `ToolException`

**Files:**
- Create: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`, `src/FlaUI.Mcp.Core/Errors/ToolException.cs`
- Test: `test/FlaUI.Mcp.Tests/Errors/ToolExceptionTests.cs`

> Implemented before the dispatcher (Task 3) because the dispatcher references these types.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Errors;
using Xunit;

namespace FlaUI.Mcp.Tests.Errors;

public class ToolExceptionTests
{
    [Fact]
    public void Carries_code_message_and_suggested_recovery()
    {
        var ex = new ToolException(ToolErrorCode.WindowNotFound, "gone", "re-list windows");
        Assert.Equal(ToolErrorCode.WindowNotFound, ex.Code);
        Assert.Equal("gone", ex.Message);
        Assert.Equal("re-list windows", ex.SuggestedRecovery);
    }

    [Fact]
    public void Suggested_recovery_is_optional()
    {
        var ex = new ToolException(ToolErrorCode.Timeout, "waited too long");
        Assert.Null(ex.SuggestedRecovery);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~ToolExceptionTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the enum**

Create `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` — enumerate the full v1 set from the spec (do not abbreviate; later phases reference these):
```csharp
namespace FlaUI.Mcp.Core.Errors;

public enum ToolErrorCode
{
    WindowNotFound,
    WindowHandleStale,
    RefNotFound,
    RefStaleUnresolvable,
    PatternUnsupported,
    ElementNotActionable,
    AmbiguousMatch,
    LaunchTimeout,
    AccessDeniedIntegrity,
    ActionBlockedPending,
    ElementDisappearedDuringAction,
    UacPromptDetected,
    Timeout
}
```

- [ ] **Step 4: Implement the exception**

Create `src/FlaUI.Mcp.Core/Errors/ToolException.cs`:
```csharp
namespace FlaUI.Mcp.Core.Errors;

/// <summary>An agent-recoverable error. Carries a stable code and an optional
/// concrete next move for the agent loop.</summary>
public sealed class ToolException : Exception
{
    public ToolErrorCode Code { get; }
    public string? SuggestedRecovery { get; }

    public ToolException(ToolErrorCode code, string message, string? suggestedRecovery = null)
        : base(message)
    {
        Code = code;
        SuggestedRecovery = suggestedRecovery;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~ToolExceptionTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Errors test/FlaUI.Mcp.Tests/Errors
git commit -m "feat(core): ToolErrorCode enum + ToolException with suggestedRecovery"
```

---

## Task 3: `AutomationDispatcher` — split query/action contexts with timeout abandonment

**Files:**
- Create: `src/FlaUI.Mcp.Core/Threading/AutomationDispatcher.cs`
- Test: `test/FlaUI.Mcp.Tests/Threading/AutomationDispatcherTests.cs`
- Depends on: Task 1 (`StaThreadContext`), Task 2 (`ToolException`/`ToolErrorCode`).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Threading;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Threading;

public class AutomationDispatcherTests
{
    [Fact]
    public async Task Query_and_action_run_on_distinct_STA_threads()
    {
        using var d = new AutomationDispatcher();
        var q = await d.RunQueryAsync(() => Environment.CurrentManagedThreadId);
        var a = await d.RunActionAsync(() => Environment.CurrentManagedThreadId, timeoutMs: 1000);
        Assert.NotEqual(q, a);
    }

    [Fact]
    public async Task Blocking_action_throws_ActionBlockedPending_after_timeout()
    {
        using var d = new AutomationDispatcher();
        using var gate = new ManualResetEventSlim(false);
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => d.RunActionAsync(() => { gate.Wait(); return 0; }, timeoutMs: 100));
        Assert.Equal(ToolErrorCode.ActionBlockedPending, ex.Code);
        gate.Set(); // release the parked worker so Dispose can join
    }

    [Fact]
    public async Task Query_context_stays_responsive_while_an_action_is_blocked()
    {
        using var d = new AutomationDispatcher();
        using var gate = new ManualResetEventSlim(false);
        // Start a blocking action (do not await to completion yet).
        var blocked = Assert.ThrowsAsync<ToolException>(
            () => d.RunActionAsync(() => { gate.Wait(); return 0; }, timeoutMs: 100));
        // The query context must still answer quickly.
        var answered = await d.RunQueryAsync(() => 42);
        Assert.Equal(42, answered);
        await blocked;
        gate.Set();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AutomationDispatcherTests`
Expected: FAIL — `AutomationDispatcher` does not exist.

- [ ] **Step 3: Implement `AutomationDispatcher`**

```csharp
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Threading;

/// <summary>
/// Marshals all UIA/COM work onto dedicated STA threads. A read/query context that
/// must stay responsive, and a separate action context for potentially-blocking calls
/// (InvokePattern.Invoke can block until a modal it opened is dismissed). Action calls
/// use a timeout: past it they surface ACTION_BLOCKED_PENDING while the worker stays
/// parked on the pending COM call, leaving the query context live.
/// </summary>
public sealed class AutomationDispatcher : IDisposable
{
    private readonly StaThreadContext _query = new("uia-query-sta");
    private readonly StaThreadContext _action = new("uia-action-sta");

    public Task<T> RunQueryAsync<T>(Func<T> func) => _query.RunAsync(func);
    public Task RunQueryAsync(Action action) => _query.RunAsync(action);

    public async Task<T> RunActionAsync<T>(Func<T> func, int timeoutMs)
    {
        var work = _action.RunAsync(func);
        var done = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (done != work)
            throw new ToolException(
                ToolErrorCode.ActionBlockedPending,
                "Action did not return within the timeout; it likely opened a modal dialog.",
                suggestedRecovery: "snapshot the window to see the dialog, then act on it");
        return await work; // observe exceptions if it actually completed
    }

    public Task RunActionAsync(Action action, int timeoutMs)
        => RunActionAsync(() => { action(); return true; }, timeoutMs);

    public void Dispose()
    {
        _query.Dispose();
        _action.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AutomationDispatcherTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Threading/AutomationDispatcher.cs test/FlaUI.Mcp.Tests/Threading/AutomationDispatcherTests.cs
git commit -m "feat(core): AutomationDispatcher - split query/action STA with timeout abandonment"
```

---

## Task 4: `FlaUI.Mcp.TestApp` — deterministic WPF target

**Files:**
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml`, `MainWindow.xaml.cs`

- [ ] **Step 1: Define the window with known AutomationIds**

Replace `test/FlaUI.Mcp.TestApp/MainWindow.xaml` contents:
```xml
<Window x:Class="FlaUI.Mcp.TestApp.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="FlaUI.Mcp TestApp" Height="400" Width="600"
        AutomationProperties.AutomationId="MainWindow">
    <StackPanel Margin="12">
        <TextBox x:Name="Input" AutomationProperties.AutomationId="Input" Margin="0,0,0,8"/>
        <Button x:Name="OkButton" Content="OK"
                AutomationProperties.AutomationId="OkButton" Click="OkButton_Click"/>
        <TextBlock x:Name="Status" AutomationProperties.AutomationId="Status" Text="ready"/>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Implement the click handler**

Replace `test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs` contents:
```csharp
using System.Windows;

namespace FlaUI.Mcp.TestApp;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OkButton_Click(object sender, RoutedEventArgs e)
        => Status.Text = $"clicked: {Input.Text}";
}
```

- [ ] **Step 3: Build the TestApp**

Run: `dotnet build test/FlaUI.Mcp.TestApp`
Expected: `Build succeeded`. Note the output exe path (e.g. `test/FlaUI.Mcp.TestApp/bin/Debug/net10.0-windows/FlaUI.Mcp.TestApp.exe`).

- [ ] **Step 4: Commit**

```bash
git add test/FlaUI.Mcp.TestApp
git commit -m "test: WPF TestApp with known AutomationIds (Input/OkButton/Status)"
```

---

## Task 5: `WindowHandle` + `TestAppFixture`

**Files:**
- Create: `src/FlaUI.Mcp.Core/Windows/WindowHandle.cs`
- Create: `test/FlaUI.Mcp.Tests/TestAppFixture.cs`

- [ ] **Step 1: Implement `WindowHandle` (id type, no UIA dependency)**

```csharp
namespace FlaUI.Mcp.Core.Windows;

/// <summary>An opaque per-server window handle id like "w1". Namespaced, monotonic.</summary>
public readonly record struct WindowHandle(string Id)
{
    public override string ToString() => Id;
}
```

- [ ] **Step 2: Implement `TestAppFixture` (launches/kills the TestApp exe for a test class)**

```csharp
using System.Diagnostics;

namespace FlaUI.Mcp.Tests;

/// <summary>Launches the built TestApp exe and ensures it is killed after tests.</summary>
public sealed class TestAppFixture : IDisposable
{
    public Process Process { get; }
    public string ExePath { get; }

    public TestAppFixture()
    {
        // test assembly runs from test/FlaUI.Mcp.Tests/bin/Debug/net10.0-windows
        var root = AppContext.BaseDirectory;
        ExePath = Path.GetFullPath(Path.Combine(
            root, "..", "..", "..", "..", "..",
            "test", "FlaUI.Mcp.TestApp", "bin", "Debug", "net10.0-windows",
            "FlaUI.Mcp.TestApp.exe"));
        if (!File.Exists(ExePath))
            throw new FileNotFoundException(
                $"TestApp not built at {ExePath}. Run: dotnet build test/FlaUI.Mcp.TestApp");

        Process = Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true })!;
        Process.WaitForInputIdle(5000);
        Thread.Sleep(500); // let the window realize
    }

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build`
Expected: `Build succeeded`. (No behavior test yet — the fixture is exercised by Tasks 6/7/9.)

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowHandle.cs test/FlaUI.Mcp.Tests/TestAppFixture.cs
git commit -m "feat(core): WindowHandle id type; test: TestAppFixture launcher"
```

---

## Task 6: `WindowManager` — discovery, registry, Process.Exited invalidation

**Files:**
- Create: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs`

`WindowManager` runs all UIA work through the `AutomationDispatcher` (query context). It owns the `UIA3Automation` instance (created on the STA thread) and the `w# → live window` registry.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

public class WindowManagerTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WindowManagerTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task ListWindows_includes_the_TestApp()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var windows = await mgr.ListWindowsAsync();
        Assert.Contains(windows, w => w.Title.Contains("TestApp"));
    }

    [Fact]
    public async Task OpenByPid_registers_a_handle_then_resolves_to_a_live_window()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        Assert.StartsWith("w", handle.Id);
        var title = await mgr.RunOnWindowAsync(handle, w => w.Title);
        Assert.Contains("TestApp", title);
    }

    [Fact]
    public async Task RunOnWindow_after_invalidate_throws_WindowHandleStale()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        mgr.Invalidate(handle); // simulates the Process.Exited path deterministically
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => mgr.RunOnWindowAsync(handle, w => w.Title));
        Assert.Equal(ToolErrorCode.WindowHandleStale, ex.Code);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter FullyQualifiedName~WindowManagerTests`
Expected: FAIL — `WindowManager` does not exist.

- [ ] **Step 3: Implement `WindowManager`**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.UIA3;

namespace FlaUI.Mcp.Core.Windows;

public sealed record WindowInfo(string Title, string ProcessName, int Pid, bool IsForeground);

public sealed class WindowManager : IDisposable
{
    private readonly AutomationDispatcher _dispatcher;
    private readonly UIA3Automation _automation;
    private readonly ConcurrentDictionary<string, Window> _handles = new();
    private readonly ConcurrentDictionary<string, Process> _watched = new();
    private int _counter;

    public WindowManager(AutomationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // UIA objects must be created on the STA thread that uses them.
        _automation = _dispatcher.RunQueryAsync(() => new UIA3Automation()).GetAwaiter().GetResult();
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() =>
        _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
        {
            var desktop = _automation.GetDesktop();
            var focused = _automation.FocusedElement();
            var list = new List<WindowInfo>();
            foreach (var child in desktop.FindAllChildren())
            {
                var w = child.AsWindow();
                if (string.IsNullOrEmpty(w.Title)) continue;
                int pid = w.Properties.ProcessId.ValueOrDefault;
                bool fg = focused != null && focused.Properties.ProcessId.ValueOrDefault == pid;
                list.Add(new WindowInfo(w.Title, SafeProcessName(pid), pid, fg));
            }
            return list;
        });

    public Task<WindowHandle> OpenByPidAsync(int pid) =>
        _dispatcher.RunQueryAsync(() =>
        {
            var match = _automation.GetDesktop().FindAllChildren()
                .Select(c => c.AsWindow())
                .FirstOrDefault(w => w.Properties.ProcessId.ValueOrDefault == pid
                                     && !string.IsNullOrEmpty(w.Title))
                ?? throw new ToolException(ToolErrorCode.WindowNotFound,
                       $"No window for pid {pid}.", "re-list windows");
            return Register(match, pid);
        });

    public Task<T> RunOnWindowAsync<T>(WindowHandle handle, Func<Window, T> func) =>
        _dispatcher.RunQueryAsync(() =>
        {
            if (!_handles.TryGetValue(handle.Id, out var w))
                throw new ToolException(ToolErrorCode.WindowHandleStale,
                    $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
            return func(w);
        });

    public void Invalidate(WindowHandle handle)
    {
        _handles.TryRemove(handle.Id, out _);
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
    }

    internal WindowHandle Register(Window window, int pid)
    {
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _handles[id] = window;
        TryWatchProcessExit(id, pid);
        return new WindowHandle(id);
    }

    private void TryWatchProcessExit(string id, int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => Invalidate(new WindowHandle(id));
            _watched[id] = proc;
        }
        catch { /* process already gone; next RunOnWindow surfaces WindowHandleStale */ }
    }

    private static string SafeProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "unknown"; }
    }

    public void Dispose()
    {
        foreach (var p in _watched.Values) try { p.Dispose(); } catch { }
        _dispatcher.RunQueryAsync(() => _automation.Dispose()).GetAwaiter().GetResult();
    }
}
```

> Verify against the installed FlaUI version: `desktop.FindAllChildren()`, `child.AsWindow()`, `w.Properties.ProcessId.ValueOrDefault`, `_automation.FocusedElement()`. If a member name differs, adjust the access but keep the method signatures and the `WindowInfo`/`WindowHandle` shapes identical (the contract Phases 2–6 depend on). If `STATE_MISMATCH`: a member truly absent in this FlaUI version → STOP and report rather than inventing.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~WindowManagerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs
git commit -m "feat(core): WindowManager - discovery, w# registry, Process.Exited invalidation"
```

---

## Task 7: Window operations — open(by title/pid)/launch/focus/close

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowOperationsTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

public class WindowOperationsTests
{
    [Fact]
    public async Task LaunchApp_returns_a_handle_to_a_window_with_a_title()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (handle, _) = await mgr.LaunchAppAsync("notepad.exe", args: null, timeoutMs: 8000);
        try
        {
            var title = await mgr.RunOnWindowAsync(handle, w => w.Title);
            Assert.False(string.IsNullOrEmpty(title));
        }
        finally { await mgr.CloseAsync(handle); }
    }

    [Fact]
    public async Task LaunchApp_with_bad_path_throws()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        await Assert.ThrowsAsync<ToolException>(
            () => mgr.LaunchAppAsync("C:/no/such/app.exe", null, 2000));
    }

    [Fact]
    public async Task OpenByTitle_with_multiple_matches_throws_AmbiguousMatch()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (h1, _) = await mgr.LaunchAppAsync("notepad.exe", null, 8000);
        var (h2, _) = await mgr.LaunchAppAsync("notepad.exe", null, 8000);
        try
        {
            var ex = await Assert.ThrowsAsync<ToolException>(
                () => mgr.OpenByTitleAsync("Untitled - Notepad"));
            Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        }
        finally { await mgr.CloseAsync(h1); await mgr.CloseAsync(h2); }
    }
}
```

> Environment note: "Untitled - Notepad" is the classic Win32 Notepad title. On Windows 11 the Store Notepad may title differently, run single-instance, or use tabs (one window). If both launches collapse to one window, the ambiguity path is unreachable via Notepad — in that case retarget this test at two `FlaUI.Mcp.TestApp` instances (same "FlaUI.Mcp TestApp" title), which are guaranteed multi-instance, and assert on that title. Prefer the TestApp form if Notepad is flaky in your environment.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~WindowOperationsTests`
Expected: FAIL — `LaunchAppAsync`/`OpenByTitleAsync`/`CloseAsync` do not exist.

- [ ] **Step 3: Add the operations to `WindowManager`**

Add these members inside the `WindowManager` class:
```csharp
public async Task<(WindowHandle handle, int pid)> LaunchAppAsync(string path, string? args, int timeoutMs)
{
    Process proc;
    try
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        proc = Process.Start(psi)
               ?? throw new ToolException(ToolErrorCode.LaunchTimeout, $"Failed to start {path}.", "verify the path");
    }
    catch (ToolException) { throw; }
    catch (Exception ex)
    {
        throw new ToolException(ToolErrorCode.LaunchTimeout, $"Could not launch {path}: {ex.Message}", "verify the path");
    }

    try { proc.WaitForInputIdle(Math.Min(timeoutMs, 5000)); } catch { /* console/no message loop */ }

    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        var handle = await TryOpenByPidQuiet(proc.Id);
        if (handle is { } h) return (h, proc.Id);
        await Task.Delay(150);
    }
    throw new ToolException(ToolErrorCode.LaunchTimeout,
        $"{path} started but showed no titled window within {timeoutMs} ms.",
        "increase timeoutMs or check for a splash screen");
}

private Task<WindowHandle?> TryOpenByPidQuiet(int pid) =>
    _dispatcher.RunQueryAsync<WindowHandle?>(() =>
    {
        var match = _automation.GetDesktop().FindAllChildren()
            .Select(c => c.AsWindow())
            .FirstOrDefault(w => w.Properties.ProcessId.ValueOrDefault == pid
                                 && !string.IsNullOrEmpty(w.Title));
        return match is null ? (WindowHandle?)null : Register(match, pid);
    });

public Task<WindowHandle> OpenByTitleAsync(string title) =>
    _dispatcher.RunQueryAsync(() =>
    {
        var matches = _automation.GetDesktop().FindAllChildren()
            .Select(c => c.AsWindow())
            .Where(w => w.Title == title)
            .ToList();
        if (matches.Count == 0)
            throw new ToolException(ToolErrorCode.WindowNotFound, $"No window titled '{title}'.", "re-list windows");
        if (matches.Count > 1)
        {
            var candidates = string.Join("; ", matches.Select(m =>
                $"pid={m.Properties.ProcessId.ValueOrDefault} bounds={m.BoundingRectangle}"));
            throw new ToolException(ToolErrorCode.AmbiguousMatch,
                $"{matches.Count} windows titled '{title}': {candidates}",
                "re-open by:pid using one of the listed pids");
        }
        var win = matches[0];
        return Register(win, win.Properties.ProcessId.ValueOrDefault);
    });

public Task FocusAsync(WindowHandle handle) =>
    RunOnWindowAsync(handle, w => { w.Focus(); w.SetForeground(); return true; });

public async Task CloseAsync(WindowHandle handle)
{
    try { await RunOnWindowAsync(handle, w => { w.Close(); return true; }); }
    catch (ToolException) { /* already gone */ }
    finally { Invalidate(handle); }
}
```
Add `using System.Diagnostics;` at the top of the file if not already present (it is, from Task 6).

> Verify FlaUI members: `w.Focus()`, `w.SetForeground()`, `w.Close()`, `w.BoundingRectangle`. Keep signatures stable.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~WindowOperationsTests`
Expected: PASS (3 tests). If the ambiguity test is environment-flaky on Notepad, switch it to two TestApp instances per the Step 1 note and re-run.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowOperationsTests.cs
git commit -m "feat(core): window open/launch(splash)/focus/close with AmbiguousMatch + LaunchTimeout"
```

---

## Task 8: `SessionManager` — handle/process tracking + lifecycle + kill policy

**Files:**
- Create: `src/FlaUI.Mcp.Core/Session/SessionManager.cs`
- Test: `test/FlaUI.Mcp.Tests/Session/SessionManagerTests.cs`

`SessionManager` is per-connection. It tracks which handles and which **server-owned** processes (from launch) belong to a connection, and on termination frees handles and applies `killSpawnedOnDisconnect`. It is injected with delegates (kill/free) so it is unit-testable without real processes or UIA.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlaUI.Mcp.Core.Session;
using Xunit;

namespace FlaUI.Mcp.Tests.Session;

public class SessionManagerTests
{
    [Fact]
    public void Terminate_with_kill_policy_on_kills_owned_processes_and_frees_handles()
    {
        var killed = new List<int>();
        var freed = new List<string>();
        var session = new SessionManager(
            killSpawnedOnDisconnect: true,
            killProcess: killed.Add,
            freeHandle: freed.Add);

        session.TrackHandle("w1");
        session.TrackOwnedProcess("w1", pid: 1234);

        session.Terminate();

        Assert.Contains("w1", freed);
        Assert.Contains(1234, killed);
    }

    [Fact]
    public void Terminate_with_kill_policy_off_frees_handles_but_keeps_processes()
    {
        var killed = new List<int>();
        var freed = new List<string>();
        var session = new SessionManager(
            killSpawnedOnDisconnect: false,
            killProcess: killed.Add,
            freeHandle: freed.Add);

        session.TrackHandle("w1");
        session.TrackOwnedProcess("w1", pid: 1234);

        session.Terminate();

        Assert.Contains("w1", freed);
        Assert.Empty(killed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SessionManagerTests`
Expected: FAIL — `SessionManager` does not exist.

- [ ] **Step 3: Implement `SessionManager`**

```csharp
using System.Collections.Concurrent;

namespace FlaUI.Mcp.Core.Session;

/// <summary>Per-connection lifecycle. Frees handles on termination always; kills
/// server-owned spawned processes only when policy allows (default on for stdio,
/// off for shared HTTP — auto-killing a user's app under a shared server is destructive).</summary>
public sealed class SessionManager
{
    private readonly bool _killSpawnedOnDisconnect;
    private readonly Action<int> _killProcess;
    private readonly Action<string> _freeHandle;
    private readonly ConcurrentDictionary<string, byte> _handles = new();
    private readonly ConcurrentDictionary<string, int> _ownedProcesses = new();

    public SessionManager(bool killSpawnedOnDisconnect, Action<int> killProcess, Action<string> freeHandle)
    {
        _killSpawnedOnDisconnect = killSpawnedOnDisconnect;
        _killProcess = killProcess;
        _freeHandle = freeHandle;
    }

    public void TrackHandle(string handleId) => _handles[handleId] = 0;
    public void TrackOwnedProcess(string handleId, int pid) => _ownedProcesses[handleId] = pid;

    public void Terminate()
    {
        foreach (var id in _handles.Keys) _freeHandle(id);
        if (_killSpawnedOnDisconnect)
            foreach (var pid in _ownedProcesses.Values)
                try { _killProcess(pid); } catch { }
        _handles.Clear();
        _ownedProcesses.Clear();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SessionManagerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Session test/FlaUI.Mcp.Tests/Session
git commit -m "feat(core): SessionManager - per-connection lifecycle + killSpawnedOnDisconnect policy"
```

---

## Task 9: MCP server host + stdio transport + `WindowTools`

**Files:**
- Create: `src/FlaUI.Mcp.Server/Tools/WindowTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs`
- Test: `test/FlaUI.Mcp.Tests/Server/WindowToolsTests.cs`

Tools are thin: they call `WindowManager`, serialize results to JSON strings, and convert `ToolException` into a structured error string (so the agent sees `{error, message, suggestedRecovery}`).

- [ ] **Step 1: Implement `WindowTools` (the thin adapter)**

```csharp
using System.ComponentModel;
using System.Text.Json;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class WindowTools
{
    private readonly WindowManager _windows;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public WindowTools(WindowManager windows) => _windows = windows;

    [McpServerTool, Description("List top-level desktop windows with title, process, and pid.")]
    public Task<string> DesktopListWindows() => Guard(async () =>
        Ok(await _windows.ListWindowsAsync()));

    [McpServerTool, Description("Open a window by pid or title and return its handle (e.g. w1).")]
    public Task<string> DesktopOpenWindow(
        [Description("Selector kind: \"pid\" or \"title\".")] string by,
        [Description("The pid (as text) or the exact window title.")] string value)
        => Guard(async () =>
        {
            var handle = by switch
            {
                "pid"   => await _windows.OpenByPidAsync(int.Parse(value)),
                "title" => await _windows.OpenByTitleAsync(value),
                _ => throw new ToolException(ToolErrorCode.WindowNotFound,
                        $"Unknown selector '{by}'.", "use by=pid or by=title")
            };
            return Ok(new { handle = handle.Id });
        });

    [McpServerTool, Description("Launch an app and return a handle to its main window.")]
    public Task<string> DesktopLaunchApp(
        [Description("Executable path.")] string path,
        [Description("Optional arguments.")] string? args = null,
        [Description("Max ms to wait for a titled window.")] int timeoutMs = 10000)
        => Guard(async () =>
        {
            var (handle, pid) = await _windows.LaunchAppAsync(path, args, timeoutMs);
            return Ok(new { handle = handle.Id, pid });
        });

    [McpServerTool, Description("Bring a window to the foreground.")]
    public Task<string> DesktopFocusWindow([Description("Window handle, e.g. w1.")] string window)
        => Guard(async () => { await _windows.FocusAsync(new WindowHandle(window)); return Ok(new { ok = true }); });

    [McpServerTool, Description("Close a window and free its handle.")]
    public Task<string> DesktopCloseWindow([Description("Window handle, e.g. w1.")] string window)
        => Guard(async () => { await _windows.CloseAsync(new WindowHandle(window)); return Ok(new { ok = true }); });

    private static string Ok(object payload) => JsonSerializer.Serialize(payload, Json);

    private static async Task<string> Guard(Func<Task<string>> body)
    {
        try { return await body(); }
        catch (ToolException ex)
        {
            return JsonSerializer.Serialize(
                new { error = ex.Code.ToString(), message = ex.Message, suggestedRecovery = ex.SuggestedRecovery }, Json);
        }
        catch (Exception ex)
        {
            // Unexpected (e.g. FormatException on a bad pid, or a COM error) — map at the
            // boundary so a single bad call never kills the process or escapes unmapped.
            return JsonSerializer.Serialize(
                new { error = "INTERNAL", message = ex.Message, suggestedRecovery = (string?)"re-check arguments and retry" }, Json);
        }
    }
}
```

- [ ] **Step 2: Wire the host in `Program.cs`**

Replace `src/FlaUI.Mcp.Server/Program.cs`:
```csharp
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
```

> If the installed `ModelContextProtocol` version names these differently (e.g. `WithTools<WindowTools>()` instead of an assembly scan, or a different transport extension), adjust ONLY this wiring to match the package; the tool class and Core stay as written. Confirm the exact API from the restored package before editing. If the symbol genuinely does not exist in the installed version → STOP and report rather than guessing a replacement.

- [ ] **Step 3: Build to verify host + SDK API compile**

Run: `dotnet build src/FlaUI.Mcp.Server`
Expected: `Build succeeded`. If it fails on an MCP SDK symbol, fix the wiring per the Step 2 note and rebuild.

- [ ] **Step 4: Write an in-process tool test (no MCP transport needed)**

This phase validates tool behavior by calling `WindowTools` directly (full MCP-client-over-stdio integration is deferred to Phase 5, where the surface is larger). Create `test/FlaUI.Mcp.Tests/Server/WindowToolsTests.cs`:
```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class WindowToolsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WindowToolsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task ListWindows_returns_json_containing_TestApp()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var tools = new WindowTools(mgr);
        var json = await tools.DesktopListWindows();
        Assert.Contains("TestApp", json);
    }

    [Fact]
    public async Task OpenWindow_with_bad_pid_returns_structured_error_with_recovery()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var tools = new WindowTools(mgr);
        var json = await tools.DesktopOpenWindow("pid", "99999999");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("WindowNotFound", doc.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("suggestedRecovery").GetString()));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter FullyQualifiedName~WindowToolsTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Manual smoke (optional but recommended)**

Run: `dotnet run --project src/FlaUI.Mcp.Server`
Expected: process starts and waits on stdio (no crash). Ctrl+C to exit. (Full MCP-client handshake is exercised in Phase 5.)

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server test/FlaUI.Mcp.Tests/Server
git commit -m "feat(server): stdio MCP host + WindowTools (list/open/launch/focus/close) with structured errors"
```

---

## Task 10: Full-suite green + phase wrap

- [ ] **Step 1: Build TestApp then run the entire suite**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test`
Expected: all tests PASS — StaThreadContext 3, ToolException 2, AutomationDispatcher 3, WindowManager 3, WindowOperations 3, SessionManager 2, WindowTools 2 (21 total).

- [ ] **Step 2: Update the execution-status memory**

Record Phase 1 complete with the final commit SHA and set the resume point to "Phase 2 — write Perception (SnapshotEngine + RefRegistry) plan, gated on Phase 1 merged" in the project execution-status memory.

- [ ] **Step 3: Final phase commit (if any uncommitted docs)**

```bash
git add -A && git commit -m "docs: Phase 1 foundation complete" || echo "nothing to commit"
```

---

## Notes for the implementer

- **STA discipline is absolute.** Never touch a FlaUI `AutomationElement`/`Window` or the `UIA3Automation` from a thread other than through `AutomationDispatcher`. Cross-thread COM access throws `RPC_E_WRONG_THREAD`. Every UIA access in this plan is already wrapped in `RunQueryAsync`; keep it that way.
- **External API caveats are flagged inline** for the two libraries whose exact surface may drift: FlaUI member names and the `ModelContextProtocol` host extensions. Verify by compiling; adjust the call site but never the documented method signatures or JSON shapes (the contract Phases 2–6 depend on). If a needed member is genuinely absent, STOP and report — do not invent a substitute.
- **Tests need an interactive desktop session** (real windows, input idle). They will not pass headless/locked; run them in an interactive Windows session. CI must use an interactive Windows runner for the window-touching tests.
- **Do not implement Phases 2–6 here.** This plan ends at a working window-management server.
```
