# Structured Server Log Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The flaui-mcp server writes a durable JSON Lines log that an agent can read after a failed session to find the defect, with no user content on disk.

**Architecture:** Core gets a tiny logging vocabulary (`LogEvent.Emit` with `LogField`s, three attributes) and emits a closed set of events at a few decision points through optional `ILogger<T>` constructor parameters. Server owns everything else: an in-repo `ILoggerProvider` that writes size-capped JSONL segments from a background thread, a masker that hides UI text and content, an MCP `CallToolFilter` that frames each tool call with a correlation id, and the `log` pointer on tool error JSON.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), Microsoft.Extensions.Logging 10.0.9, ModelContextProtocol 1.4.0, System.Text.Json, xUnit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-11-07-structured-logging-design.md` (approved 2026-11-07, panel GREEN). Where this plan and the spec differ, the "Spec adjustments" section below says why; the spec is otherwise the oracle.

---

## Conventions for every task

- **Build:** `dotnet build FlaUI.Mcp.slnx -c Debug` → expect `Build succeeded.` `Directory.Build.props` sets `TreatWarningsAsErrors=true`: a warning is a failure. Do not add `NoWarn`.
- **Targeted tests:** `dotnet test FlaUI.Mcp.slnx -c Debug --no-build --filter "FullyQualifiedName~<Class>"`.
- **Headless gate (repo's own, unchanged):** `dotnet test FlaUI.Mcp.slnx -c Debug --no-build --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"` (justfile:32 / `scripts/headless-gate.ps1:66`). Baseline before Task 1: record the passed count (expected 1143 at HEAD `21b26de`).
- **Never edit a pre-existing test** (repo rule, see the comment at `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:47-52`). New constructor parameters are therefore always OPTIONAL and LAST.
- **License header:** every new `.cs` file starts with the 5-line header used by every file in `src/` (copy it from `src/FlaUI.Mcp.Core/Threading/StaThreadContext.cs:1-5`).
- **Line endings:** keep each existing file's endings (Program.cs and docs/agent-contract.md are LF; ToolResponse.cs, StaThreadContext.cs, docs/operator-manual.md, CHANGELOG.md, TODO.md are CRLF). Count CRs with pwsh `[IO.File]::ReadAllText`, never grep (grep is unreliable in this environment).
- **Bare `catch`:** do not add new bare `catch { }` (R13). Use `catch (Exception ex) when (ex is not OutOfMemoryException)` and ignore `ex`.
- Commit after each task with a Conventional Commit subject (lefthook enforces it) and the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Spec adjustments (decided while planning against the code)

1. **The server-process test is `Category=Desktop`**, not headless. The harness it reuses (`test/FlaUI.Mcp.Tests/Server/StdioProtocolCleanlinessTests.cs:22`) is Desktop because it spawns the built exe; the repo convention wins. Every other test here is headless.
2. **`FLAUI_MCP_LOG_DIR` environment variable** overrides the log directory. The process test needs it (a known-folder lookup ignores a changed `LOCALAPPDATA`), and it matches the existing `FLAUI_MCP_*_DIR` family (`docs/operator-manual.md:130-138`).
3. **The file provider writes only Core/Server `LogEventState` records.** Framework log lines (host lifetime, SDK) stay on stderr only. This is what keeps the event set closed (spec §5.2).
4. **stderr is unchanged for framework logs; FlaUI.Mcp events reach stderr only at Warning+.** Without this filter every `tool.start` would also print to stderr.
5. **Field sets fixed against the code** (spec §11 allowed this):
   - `selector.resolve`: `path` (`selector`|`find`), `automationId`*, `name`*, `controlType`, `candidates`, and `chosen` (selector) or `returned` (find). No `culled`: neither resolver computes one (`PerceptionManager.cs:240-253`, `:703`).
   - `ref.resolve`: emitted by `RefRegistry.Lookup` (`RefRegistry.cs:88`), the one function every ref path calls; `found` means "the ref is registered for that window".
   - `wait.end`: `kind` (`wait_for`|`wait_for_stable`), `condition`, `by`, `value`*, `ms`, `satisfied`, `code?`. No `polls`: the loops keep no per-call counter (`WaitCoordinator.cs:43` is instance-wide and documented not thread-safe).
   - `capture.decision`: `scope`, `ok`, `method`, `warnings` (codes), `code?`. A breaker diversion shows up as the `ScrapeFallbackTargetUnresponsive` warning code, so no separate `breaker` field.
   - `input.decision`: `action`, `allowed`, `code?`, `budgetLeft`. `lease` is not re-read (a second file read per input); a refusal for a lease reason is visible as `code: InputNotLeased`.
   - `action.blocked`: emitted where `WindowManager.RunOnWindowActionAsync` (`WindowManager.cs:600`) observes `ActionBlockedPending`; that is the only action wrapper that knows the window.

---

## File map

| File | New/Modify | Responsibility |
|---|---|---|
| `src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj` | Modify | add `Microsoft.Extensions.Logging.Abstractions` 10.0.9 |
| `src/FlaUI.Mcp.Core/Logging/LogAttributes.cs` | New | `[LogStructural]`, `[LogUiText]`, `[LogForegroundTarget]` |
| `src/FlaUI.Mcp.Core/Logging/LogEvent.cs` | New | `LogField`, `LogEventState`, `LogEvent.Emit/F/Ui` |
| `src/FlaUI.Mcp.Core/Threading/StaThreadContext.cs` | Modify | flow `ExecutionContext` into queued work |
| `src/FlaUI.Mcp.Core/Perception/Selector.cs` | Modify | property attributes |
| `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` | Modify | `window.bind`, `focus.attempt`, `action.blocked` |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` | Modify | `selector.resolve` |
| `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` | Modify | `ref.resolve` |
| `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs` | Modify | `wait.end` |
| `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs` | Modify | `capture.decision` |
| `src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs` | Modify | `Remaining(...)` |
| `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` | Modify | `input.decision` |
| `src/FlaUI.Mcp.Server/Logging/ContentHasher.cs` | New | keyed `{len, sha8}` |
| `src/FlaUI.Mcp.Server/Logging/LogScope.cs` | New | `LogScope` record + `LogContext` AsyncLocal |
| `src/FlaUI.Mcp.Server/Logging/LogMasker.cs` | New | UI-text / content masking of a record's fields |
| `src/FlaUI.Mcp.Server/Logging/JsonlRecordSerializer.cs` | New | one record → one ≤8 KB UTF-8 line |
| `src/FlaUI.Mcp.Server/Logging/JsonlFileLoggerProvider.cs` | New | channel, writer thread, segments, pruning |
| `src/FlaUI.Mcp.Server/Logging/ToolParameterCatalog.cs` | New | tool name → parameter classes, by reflection |
| `src/FlaUI.Mcp.Server/Logging/ArgumentClassifier.cs` | New | `JsonElement` args → logged `args` object |
| `src/FlaUI.Mcp.Server/Logging/CallSensitivity.cs` | New | per-call `sensitive` decision |
| `src/FlaUI.Mcp.Server/Logging/ToolCallLogFilter.cs` | New | `tool.start` / `tool.end` around each call |
| `src/FlaUI.Mcp.Server/Logging/ToolLog.cs` | New | static logger + pointer for `ToolResponse` |
| `src/FlaUI.Mcp.Server/Logging/HostFacts.cs` | New | DPI awareness, DPI scale, session kind |
| `src/FlaUI.Mcp.Server/Tools/*.cs` (13 files) | Modify | parameter attributes |
| `src/FlaUI.Mcp.Server/Tools/ToolResponse.cs` | Modify | `tool.exception`, `log` field |
| `src/FlaUI.Mcp.Server/ServerOptions.cs` | Modify | `--no-log`, `--log-level` |
| `src/FlaUI.Mcp.Server/Program.cs` | Modify | wiring, `server.start/stop` |
| `test/FlaUI.Mcp.Tests/Logging/*.cs` | New | all new tests |
| `docs/agent-contract.md`, `docs/operator-manual.md`, `CHANGELOG.md`, `TODO.md` | Modify | docs |

---

### Task 0: Baseline

- [ ] **Step 1:** `git status --short` → empty. `git log --oneline -1` → `21b26de` or a later docs-only commit.
- [ ] **Step 2:** `dotnet build FlaUI.Mcp.slnx -c Debug` → `Build succeeded.`
- [ ] **Step 3:** Run the headless gate (Conventions). Record `Passed: N` (expected 1143). Every later task's headless run must show `N + (tests added so far)` passed, 0 failed.

---

### Task 1: Core logging vocabulary

**Files:** Modify `src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj`; Create `src/FlaUI.Mcp.Core/Logging/LogAttributes.cs`, `src/FlaUI.Mcp.Core/Logging/LogEvent.cs`, `test/FlaUI.Mcp.Tests/Logging/CapturingLogger.cs`, `test/FlaUI.Mcp.Tests/Logging/LogEventTests.cs`.

- [ ] **Step 1: Add the package.** In `FlaUI.Mcp.Core.csproj`, the `<ItemGroup>` holding `<PackageReference Include="FlaUI.UIA3" Version="5.0.0" />` becomes:

```xml
  <ItemGroup>
    <PackageReference Include="FlaUI.UIA3" Version="5.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
  </ItemGroup>
```

- [ ] **Step 2: Write the test helper** `test/FlaUI.Mcp.Tests/Logging/CapturingLogger.cs`:

```csharp
// (license header)

using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Records every LogEventState logged through it. Thread-safe.</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, LogEventState State)> _events = new();
    public LogLevel MinLevel { get; init; } = LogLevel.Trace;

    public IReadOnlyList<(LogLevel Level, LogEventState State)> Events
    { get { lock (_gate) return _events.ToArray(); } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is LogEventState s) lock (_gate) _events.Add((logLevel, s));
    }

    public LogEventState Single(string ev)
    {
        LogEventState? found = null;
        foreach (var (_, s) in Events)
            if (s.Event == ev) { Xunit.Assert.Null(found); found = s; }
        Xunit.Assert.NotNull(found);
        return found!;
    }
}

public sealed class CapturingLogger<T> : ILogger<T>
{
    public CapturingLogger Inner { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f)
        => Inner.Log(l, e, s, x, f);
}
```

- [ ] **Step 3: Write the failing test** `test/FlaUI.Mcp.Tests/Logging/LogEventTests.cs`:

```csharp
// (license header)

using System.Linq;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class LogEventTests
{
    [Fact]
    public void Emit_records_event_name_and_fields_in_order()
    {
        var log = new CapturingLogger();
        LogEvent.Emit(log, LogLevel.Information, "window.bind",
            LogEvent.F("handle", "w3"), LogEvent.Ui("title", "Untitled - Notepad"));
        var s = log.Single("window.bind");
        Assert.Equal(new[] { "handle", "title" }, s.Fields.Select(f => f.Key));
        Assert.False(s.Fields[0].UiText);
        Assert.True(s.Fields[1].UiText);
    }

    [Fact]
    public void Emit_is_a_no_op_for_a_null_or_disabled_logger()
    {
        LogEvent.Emit(null, LogLevel.Information, "x");
        var log = new CapturingLogger { MinLevel = LogLevel.Warning };
        LogEvent.Emit(log, LogLevel.Information, "x");
        Assert.Empty(log.Events);
    }

    [Fact]
    public void ToString_never_prints_ui_text()
    {
        // The console (stderr) provider renders the state with ToString; UI text must not reach it.
        var s = new LogEventState("e", new[] { LogEvent.F("a", 1), LogEvent.Ui("title", "SECRET-TITLE") });
        Assert.DoesNotContain("SECRET-TITLE", s.ToString());
        Assert.Contains("a=1", s.ToString());
    }

    [Fact]
    public void A_throwing_logger_never_escapes_Emit()
    {
        LogEvent.Emit(new ThrowingLogger(), LogLevel.Information, "x");
    }

    private sealed class ThrowingLogger : ILogger
    {
        public System.IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, System.Exception? x,
            System.Func<TState, System.Exception?, string> f) => throw new System.InvalidOperationException("boom");
    }
}
```

- [ ] **Step 4:** Build → FAIL (`LogEvent` does not exist).
- [ ] **Step 5: Implement** `src/FlaUI.Mcp.Core/Logging/LogAttributes.cs`:

```csharp
// (license header)

namespace FlaUI.Mcp.Core.Logging;

/// <summary>Logged raw: handles, refs, enum-like words, chords, timeouts, flags. See the structured
/// logging spec §6.1. An UNMARKED string is content and is hashed (fail closed).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class LogStructuralAttribute : Attribute { }

/// <summary>Text that comes FROM the UI (titles, element names, automation ids, find queries). Logged raw
/// unless the call's target window is sensitive (spec §6.3).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class LogUiTextAttribute : Attribute { }

/// <summary>On a tool method: when its `window` argument is omitted it acts on the current FOREGROUND
/// window, so the call's sensitivity is decided by the foreground process (spec §6.3).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LogForegroundTargetAttribute : Attribute { }
```

- [ ] **Step 6: Implement** `src/FlaUI.Mcp.Core/Logging/LogEvent.cs`:

```csharp
// (license header)

using System.Collections;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Core.Logging;

/// <summary>One field of a structured event. `UiText` marks a value that comes from the UI; the file
/// provider masks it when the call is sensitive (spec §5.2, the starred fields).</summary>
public readonly record struct LogField(string Key, object? Value, bool UiText = false);

/// <summary>The state object of every structured event. The file provider writes ONLY records whose state
/// is a LogEventState; anything else (framework logs) stays on stderr.</summary>
public sealed class LogEventState : IReadOnlyList<KeyValuePair<string, object?>>
{
    public LogEventState(string ev, IReadOnlyList<LogField> fields) { Event = ev; Fields = fields; }

    public string Event { get; }
    public IReadOnlyList<LogField> Fields { get; }

    public int Count => Fields.Count + 1;
    public KeyValuePair<string, object?> this[int index] =>
        index == 0 ? new("ev", Event) : new(Fields[index - 1].Key, Fields[index - 1].UiText ? "<ui>" : Fields[index - 1].Value);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>What the console (stderr) provider prints. UI text is never rendered.</summary>
    public override string ToString() =>
        Event + " " + string.Join(" ", Fields.Select(f => $"{f.Key}={(f.UiText ? "<ui>" : f.Value)}"));
}

public static class LogEvent
{
    public static LogField F(string key, object? value) => new(key, value);
    public static LogField Ui(string key, string? value) => new(key, value, UiText: true);

    /// <summary>Log one structured event. Never throws: a broken logger must not change a tool's outcome
    /// (spec §7).</summary>
    public static void Emit(ILogger? logger, LogLevel level, string ev, params LogField[] fields)
    {
        if (logger is null) return;
        try
        {
            if (!logger.IsEnabled(level)) return;
            logger.Log(level, default, new LogEventState(ev, fields), null, static (s, _) => s.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; }
    }
}
```

- [ ] **Step 7:** Build → `Build succeeded.` Run `--filter "FullyQualifiedName~LogEventTests"` → 4 passed.
- [ ] **Step 8:** Commit `feat(core): structured log event vocabulary`.

---

### Task 2: Flow ExecutionContext through the query STA

**Files:** Modify `src/FlaUI.Mcp.Core/Threading/StaThreadContext.cs:31-39`; Create `test/FlaUI.Mcp.Tests/Logging/StaContextFlowTests.cs`.

- [ ] **Step 1: Failing test:**

```csharp
// (license header)

using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class StaContextFlowTests
{
    private static readonly AsyncLocal<string?> Probe = new();

    [Fact]
    public async Task An_AsyncLocal_set_by_the_caller_is_visible_inside_queued_STA_work()
    {
        using var ctx = new StaThreadContext("flow-sta");
        Probe.Value = "c-0000abcd";
        var seen = await ctx.RunAsync(() => Probe.Value);
        Assert.Equal("c-0000abcd", seen);
    }

    [Fact]
    public async Task Each_work_item_sees_its_own_callers_value()
    {
        using var ctx = new StaThreadContext("flow-sta2");
        Probe.Value = "first";
        var a = ctx.RunAsync(() => Probe.Value);
        Probe.Value = "second";
        var b = ctx.RunAsync(() => Probe.Value);
        Assert.Equal("first", await a);
        Assert.Equal("second", await b);
    }
}
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~StaContextFlowTests"` → both FAIL (the STA thread was started at construction, so it sees `null`).
- [ ] **Step 3: Implement.** Replace the whole `RunAsync<T>` method (lines 31-39):

```csharp
    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Capture the CALLER's ExecutionContext and run the work inside it, so AsyncLocal state the caller
        // set (the structured log's correlation scope) is visible on this STA. BlockingCollection does not
        // flow it: the thread only ever sees the context it was STARTED with. Null when flow is suppressed.
        var captured = ExecutionContext.Capture();
        _queue.Add(() =>
        {
            void Work()
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }
            if (captured is null) Work();
            else ExecutionContext.Run(captured, static s => ((Action)s!)(), (Action)Work);
        });
        return tcs.Task;
    }
```

- [ ] **Step 4:** Run the 2 tests → PASS. Run `--filter "FullyQualifiedName~StaThreadContextTests|FullyQualifiedName~AutomationDispatcherTests"` → all pass.
- [ ] **Step 5: Mutation check.** Temporarily replace the `if (captured is null) ... else ...` pair with `Work();` → both new tests FAIL. Restore.
- [ ] **Step 6:** Commit `feat(core): flow the caller's ExecutionContext into STA work`.

---

### Task 3: Hashing, scope, and masking

**Files:** Create `src/FlaUI.Mcp.Server/Logging/ContentHasher.cs`, `LogScope.cs`, `LogMasker.cs`; `test/FlaUI.Mcp.Tests/Logging/LogMaskerTests.cs`.

- [ ] **Step 1: Failing tests** `LogMaskerTests.cs`:

```csharp
// (license header)

using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class LogMaskerTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();
    private static LogMasker Masker(Func<string?, bool>? sensitiveProcess = null) =>
        new(new ContentHasher(Key), sensitiveProcess ?? (p => p == "keepass"));

    private static LogScope Scope(bool sensitive, bool content = false, bool targetless = false) =>
        new("c-00000001", "desktop_click", sensitive, content, targetless);

    [Fact]
    public void Hash_is_keyed_8_hex_and_deterministic_per_key()
    {
        var a = new ContentHasher(Key).Mask("1234");
        Assert.Equal(4, a.Len);
        Assert.Matches("^[0-9a-f]{8}$", a.Sha8);
        Assert.Equal(a, new ContentHasher(Key).Mask("1234"));
        Assert.NotEqual(a.Sha8, new ContentHasher(Enumerable.Repeat((byte)8, 32).ToArray()).Mask("1234").Sha8);
        // Not a plain SHA-256 prefix (spec §6.1: unkeyed hashes of short secrets are reversible).
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("1234"u8.ToArray()))[..8].ToLowerInvariant();
        Assert.NotEqual(plain, a.Sha8);
    }

    [Fact]
    public void Ui_text_is_raw_when_not_sensitive_and_masked_when_sensitive()
    {
        var f = new[] { LogEvent.Ui("name", "Save As"), LogEvent.F("candidates", 0) };
        var raw = Masker().Apply("selector.resolve", f, Scope(false));
        Assert.Equal("Save As", raw[0].Value);
        var masked = Masker().Apply("selector.resolve", f, Scope(true));
        Assert.IsType<MaskedValue>(masked[0].Value);
        Assert.Equal(0, masked[1].Value);
    }

    [Fact]
    public void No_scope_and_no_own_process_fails_closed()
    {
        var m = Masker().Apply("selector.resolve", new[] { LogEvent.Ui("name", "x") }, scope: null);
        Assert.IsType<MaskedValue>(m[0].Value);
    }

    [Fact]
    public void An_events_own_process_field_decides_out_of_scope()
    {
        var notepad = Masker().Apply("window.bind",
            new[] { LogEvent.F("process", "notepad"), LogEvent.Ui("title", "a.txt") }, scope: null);
        Assert.Equal("a.txt", notepad[1].Value);
        var keepass = Masker().Apply("window.bind",
            new[] { LogEvent.F("process", "keepass"), LogEvent.Ui("title", "db.kdbx") }, scope: null);
        Assert.IsType<MaskedValue>(keepass[1].Value);
    }

    [Fact]
    public void An_own_sensitive_process_masks_even_inside_a_non_sensitive_call()
    {
        var m = Masker().Apply("action.blocked",
            new[] { LogEvent.F("fgProcess", "keepass"), LogEvent.Ui("fgTitle", "Unlock") }, Scope(false));
        Assert.IsType<MaskedValue>(m[1].Value);
    }

    [Fact]
    public void Exception_message_is_masked_on_a_content_call_whatever_the_window()
    {
        // spec §6.4
        var f = new[] { LogEvent.Ui("msg", "Value 'hunter2' rejected") };
        Assert.IsType<MaskedValue>(Masker().Apply("tool.exception", f, Scope(false, content: true))[0].Value);
        Assert.Equal("Value 'hunter2' rejected", Masker().Apply("tool.exception", f, Scope(false))[0].Value);
    }

    [Fact]
    public void Internal_tool_end_message_is_masked_on_a_content_call()
    {
        var f = new[] { LogEvent.F("outcome", "INTERNAL"), LogEvent.Ui("msg", "x") };
        Assert.IsType<MaskedValue>(Masker().Apply("tool.end", f, Scope(false, content: true))[1].Value);
        var g = new[] { LogEvent.F("outcome", "SelectorNoMatch"), LogEvent.Ui("msg", "x") };
        Assert.Equal("x", Masker().Apply("tool.end", g, Scope(false, content: true))[1].Value);
    }

    [Fact]
    public void Null_ui_text_stays_null()
    {
        Assert.Null(Masker().Apply("e", new[] { LogEvent.Ui("title", null) }, Scope(true))[0].Value);
    }
}
```

- [ ] **Step 2:** Build → FAIL (types missing).
- [ ] **Step 3: Implement** `ContentHasher.cs`:

```csharp
// (license header)

using System.Security.Cryptography;
using System.Text;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>A masked value as it appears on disk: `{"len": n, "sha8": "..."}` (spec §5, §6.1).</summary>
public readonly record struct MaskedValue(int Len, string Sha8);

/// <summary>HMAC-SHA256 under a per-process random key that is never written anywhere. Equal values in
/// ONE session's files hash equal; nothing more (spec §6.1). Tests inject the key.</summary>
public sealed class ContentHasher
{
    private readonly byte[] _key;
    public ContentHasher(byte[]? key = null) => _key = key ?? RandomNumberGenerator.GetBytes(32);

    public MaskedValue Mask(string value)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return new MaskedValue(value.Length, Convert.ToHexString(mac, 0, 4).ToLowerInvariant());
    }
}
```

- [ ] **Step 4: Implement** `LogScope.cs`:

```csharp
// (license header)

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Per-call logging scope, opened by ToolCallLogFilter. `Sensitive` = UI text of this call is
/// masked (spec §6.3). `HasContentArgs` = at least one argument was masked as content (spec §6.4).
/// `Targetless` = the call had no target window to classify.</summary>
public sealed record LogScope(string Cid, string Tool, bool Sensitive, bool HasContentArgs, bool Targetless);

/// <summary>The current call's scope, for code that cannot read logger scopes (ToolResponse's `log`
/// pointer). Set by ToolCallLogFilter; flows with the async call.</summary>
public static class LogContext
{
    private static readonly AsyncLocal<LogScope?> s_current = new();
    public static LogScope? Current { get => s_current.Value; set => s_current.Value = value; }
}
```

- [ ] **Step 5: Implement** `LogMasker.cs`:

```csharp
// (license header)

using FlaUI.Mcp.Core.Logging;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Applies spec §5.2 (starred fields), §6.3 and §6.4 to one record's fields. Pure.</summary>
public sealed class LogMasker
{
    private readonly ContentHasher _hasher;
    private readonly Func<string?, bool> _isSensitiveProcess;

    public LogMasker(ContentHasher hasher, Func<string?, bool> isSensitiveProcess)
    { _hasher = hasher; _isSensitiveProcess = isSensitiveProcess; }

    public ContentHasher Hasher => _hasher;

    public IReadOnlyList<LogField> Apply(string ev, IReadOnlyList<LogField> fields, LogScope? scope)
    {
        // An event naming its OWN process (window.bind, action.blocked) is classified by it.
        string? ownProcess = null; bool hasOwn = false;
        string? outcome = null;
        foreach (var f in fields)
        {
            if (f.Key is "process" or "fgProcess") { hasOwn = true; ownProcess = f.Value as string; }
            if (f.Key == "outcome") outcome = f.Value as string;
        }
        bool sensitive = scope is null
            ? (!hasOwn || _isSensitiveProcess(ownProcess))       // no call scope, no own process: fail closed
            : scope.Sensitive || (hasOwn && _isSensitiveProcess(ownProcess));

        // §6.4: a message the server did not write, on a call that carried content.
        bool maskMsgForContent = scope is { HasContentArgs: true }
            && (ev == "tool.exception" || (ev == "tool.end" && outcome == "INTERNAL"));

        var result = new LogField[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            bool mask = f.UiText && f.Value is string && (sensitive || (f.Key == "msg" && maskMsgForContent));
            result[i] = mask ? f with { Value = _hasher.Mask((string)f.Value!) } : f;
        }
        return result;
    }
}
```

- [ ] **Step 6:** Build; run `--filter "FullyQualifiedName~LogMaskerTests"` → 8 passed.
- [ ] **Step 7:** Commit `feat(server): keyed content hash and log field masking`.

---

### Task 4: Record serializer (one line, ≤ 8 KB)

**Files:** Create `src/FlaUI.Mcp.Server/Logging/JsonlRecordSerializer.cs`; `test/FlaUI.Mcp.Tests/Logging/JsonlRecordSerializerTests.cs`.

- [ ] **Step 1: Failing tests:**

```csharp
// (license header)

using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class JsonlRecordSerializerTests
{
    private static readonly DateTime Ts = new(2026, 11, 7, 14, 3, 11, 412, DateTimeKind.Utc);
    private static readonly LogScope Call = new("c-3f9a1c2e", "desktop_click", false, false, false);

    private static JsonElement Parse(byte[] line)
    {
        Assert.Equal((byte)'\n', line[^1]);
        Assert.DoesNotContain((byte)'\n', line[..^1]);
        return JsonDocument.Parse(line.AsMemory(0, line.Length - 1)).RootElement;
    }

    [Fact]
    public void Fixed_fields_come_first_in_contract_order()
    {
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Warning, "tool.end", Call,
            new[] { LogEvent.F("outcome", "SelectorNoMatch"), LogEvent.F("ms", 2140) }), pid: 1234);
        var names = Parse(line).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "ts", "lvl", "pid", "ev", "cid", "tool", "outcome", "ms" }, names);
        var r = Parse(line);
        Assert.Equal("2026-11-07T14:03:11.412Z", r.GetProperty("ts").GetString());
        Assert.Equal("warning", r.GetProperty("lvl").GetString());
        Assert.Equal(1234, r.GetProperty("pid").GetInt32());
    }

    [Fact]
    public void No_scope_means_no_cid_or_tool()
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "server.start", null,
            Array.Empty<LogField>()), 1));
        Assert.False(r.TryGetProperty("cid", out _));
        Assert.False(r.TryGetProperty("tool", out _));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "debug")] [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")] [InlineData(LogLevel.Warning, "warning")]
    [InlineData(LogLevel.Error, "error")] [InlineData(LogLevel.Critical, "error")]
    public void Levels_map_to_four_names(LogLevel level, string expected)
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, level, "e", null, Array.Empty<LogField>()), 1));
        Assert.Equal(expected, r.GetProperty("lvl").GetString());
    }

    [Fact]
    public void Masked_value_serializes_as_len_sha8_object_and_IntPtr_as_number()
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "window.bind", null,
            new[] { LogEvent.F("hwnd", (IntPtr)0x2004C), LogEvent.F("title", new MaskedValue(12, "9b1e04aa")) }), 1));
        Assert.Equal(0x2004C, r.GetProperty("hwnd").GetInt64());
        Assert.Equal(12, r.GetProperty("title").GetProperty("len").GetInt32());
        Assert.Equal("9b1e04aa", r.GetProperty("title").GetProperty("sha8").GetString());
    }

    [Fact]
    public void A_newline_in_a_value_cannot_forge_a_record()
    {
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "e", null,
            new[] { LogEvent.F("x", "a\n{\"ev\":\"forged\"}") }), 1);
        Assert.Single(line, b => b == (byte)'\n');
    }

    [Fact]
    public void Oversized_stack_is_cut_first_and_msg_survives()
    {
        var stack = string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"   at Frame{i}()"));
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Error, "tool.exception", Call,
            new[] { LogEvent.F("exType", "System.Runtime.InteropServices.COMException"),
                    LogEvent.F("stack", stack), LogEvent.F("msg", "The remote procedure call failed.") }), 1);
        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes);
        var r = Parse(line);
        Assert.True(r.GetProperty("trunc").GetBoolean());
        Assert.Equal("The remote procedure call failed.", r.GetProperty("msg").GetString());
        Assert.StartsWith("   at Frame0()", r.GetProperty("stack").GetString());
    }

    [Fact]
    public void Oversized_args_are_omitted_last_and_fixed_fields_survive()
    {
        var args = new JsonObject();
        for (int i = 0; i < 3000; i++) args["k" + i] = i;   // many small numbers: no string to shorten
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "tool.start", Call,
            new[] { LogEvent.F("args", args) }), 1);
        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes);
        var r = Parse(line);
        Assert.True(r.GetProperty("args").GetProperty("omitted").GetInt32() > JsonlRecordSerializer.MaxRecordBytes);
        Assert.Equal("c-3f9a1c2e", r.GetProperty("cid").GetString());
    }

    [Fact]
    public void A_record_under_the_cap_is_not_marked_truncated()
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "e", null,
            new[] { LogEvent.F("a", "b") }), 1));
        Assert.False(r.TryGetProperty("trunc", out _));
    }
}
```

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: Implement** `JsonlRecordSerializer.cs`:

```csharp
// (license header)

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>One log record, fields already masked.</summary>
public sealed record LogRecord(DateTime Ts, LogLevel Level, string Ev, LogScope? Scope, IReadOnlyList<LogField> Fields);

/// <summary>Serialises a record to one UTF-8 JSON line of at most MaxRecordBytes (spec §5, §5.1).
/// System.Text.Json with its default encoder only, so control characters are escaped (no forged lines).</summary>
public static class JsonlRecordSerializer
{
    public const int MaxRecordBytes = 8192;
    private static readonly string[] Fixed = { "ts", "lvl", "pid", "ev", "cid", "tool", "outcome", "ms" };

    public static byte[] Serialize(LogRecord record, int pid)
    {
        var o = new JsonObject
        {
            ["ts"] = record.Ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["lvl"] = LevelName(record.Level),
            ["pid"] = pid,
            ["ev"] = record.Ev,
        };
        if (record.Scope is { } s) { o["cid"] = s.Cid; o["tool"] = s.Tool; }
        foreach (var f in record.Fields) o[f.Key] = ToNode(f.Value);

        var bytes = Bytes(o);
        if (bytes.Length <= MaxRecordBytes) return bytes;
        o["trunc"] = true;

        // (1) stack: keep the first frames
        if (o["stack"] is JsonValue sv && sv.TryGetValue<string>(out var stack))
        {
            var lines = stack.Split('\n');
            for (int keep = Math.Min(lines.Length, 32); keep >= 1 && (bytes = Bytes(o)).Length > MaxRecordBytes; keep /= 2)
                o["stack"] = string.Join('\n', lines.Take(keep)) + (keep < lines.Length ? "\n..." : "");
        }
        // (2) the longest raw string leaves in args and event fields (not msg, not fixed)
        while ((bytes = Bytes(o)).Length > MaxRecordBytes && LongestCuttable(o) is { } leaf && leaf.Len > 16)
            leaf.Set(leaf.Value[..(leaf.Len / 2)] + "...");
        // (3) msg, last because it carries the failure explanation
        while ((bytes = Bytes(o)).Length > MaxRecordBytes && o["msg"] is JsonValue mv && mv.TryGetValue<string>(out var msg) && msg.Length > 16)
            o["msg"] = msg[..(msg.Length / 2)] + "...";
        // (4) args replaced by its size
        if ((bytes = Bytes(o)).Length > MaxRecordBytes && o["args"] is JsonNode argsNode)
            o["args"] = new JsonObject { ["omitted"] = Encoding.UTF8.GetByteCount(argsNode.ToJsonString()) };
        // (5) last resort: only the fixed fields survive
        if ((bytes = Bytes(o)).Length > MaxRecordBytes)
        {
            foreach (var key in o.Select(p => p.Key).ToArray())
                if (Array.IndexOf(Fixed, key) < 0 && key != "trunc") o.Remove(key);
            bytes = Bytes(o);
        }
        return bytes;
    }

    private static byte[] Bytes(JsonObject o)
    {
        var json = o.ToJsonString();          // default encoder: escapes \n, \r, and other control chars
        var b = new byte[Encoding.UTF8.GetByteCount(json) + 1];
        Encoding.UTF8.GetBytes(json, 0, json.Length, b, 0);
        b[^1] = (byte)'\n';
        return b;
    }

    private static string LevelName(LogLevel l) => l switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warning",
        _ => "error",
    };

    private static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        MaskedValue m => new JsonObject { ["len"] = m.Len, ["sha8"] = m.Sha8 },
        IntPtr p => JsonValue.Create(p.ToInt64()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        IEnumerable<string> list => new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        _ => JsonValue.Create(v.ToString()),
    };

    private sealed record Leaf(string Value, Action<string> Set) { public int Len => Value.Length; }

    private static Leaf? LongestCuttable(JsonObject o)
    {
        Leaf? best = null;
        void Consider(string value, Action<string> set)
        { if (best is null || value.Length > best.Len) best = new Leaf(value, set); }
        void Walk(JsonNode? node, Action<string> set)
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<string>(out var s): Consider(s, set); break;
                case JsonObject obj:
                    if (obj.Count == 2 && obj.ContainsKey("len") && obj.ContainsKey("sha8")) break; // masked: never cut
                    foreach (var key in obj.Select(p => p.Key).ToArray()) { var k = key; Walk(obj[k], x => obj[k] = x); }
                    break;
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++) { var j = i; Walk(arr[j], x => arr[j] = x); }
                    break;
            }
        }
        foreach (var key in o.Select(p => p.Key).ToArray())
        {
            if (Array.IndexOf(Fixed, key) >= 0 || key is "msg" or "stack" or "trunc") continue;
            var k = key; Walk(o[k], x => o[k] = x);
        }
        return best;
    }
}
```

- [ ] **Step 4:** Build; run `--filter "FullyQualifiedName~JsonlRecordSerializerTests"` → 13 passed (the Theory counts 6).
- [ ] **Step 5:** Commit `feat(server): JSONL record serializer with the 8 KB cap`.

---

### Task 5: The file logger provider

**Files:** Create `src/FlaUI.Mcp.Server/Logging/JsonlFileLoggerProvider.cs`; `test/FlaUI.Mcp.Tests/Logging/JsonlFileLoggerProviderTests.cs`.

- [ ] **Step 1: Failing tests:**

```csharp
// (license header)

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public sealed class JsonlFileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-log-test-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private JsonlFileLoggerProvider Create(long segmentBytes = 5 * 1024 * 1024, int capacity = 4096,
        Func<int, DateTime, bool>? isAlive = null)
    {
        var opts = new JsonlLogOptions(_dir, Pid: 4242, ProcessStartUtc: Start, MinLevel: LogLevel.Trace)
        { SegmentBytes = segmentBytes, ChannelCapacity = capacity, IsAlive = isAlive ?? ((_, _) => false) };
        var masker = new LogMasker(new ContentHasher(new byte[32]), _ => false);
        var p = JsonlFileLoggerProvider.TryCreate(opts, masker, out var error);
        Assert.Null(error);
        return p!;
    }

    private string[] Lines() => Directory.GetFiles(_dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal)
        .SelectMany(File.ReadAllLines).ToArray();

    [Fact]
    public void Writes_one_line_per_event_to_the_pid_start_segment_file()
    {
        using (var p = Create())
        {
            var log = p.CreateLogger("x");
            LogEvent.Emit(log, LogLevel.Information, "server.start", LogEvent.F("version", "1.1.0"));
            Assert.EndsWith("flaui-mcp-4242-20261107T100000Z-01.jsonl", p.CurrentFilePath);
        }
        var line = Assert.Single(Lines());
        Assert.Equal("server.start", JsonDocument.Parse(line).RootElement.GetProperty("ev").GetString());
    }

    [Fact]
    public void Framework_records_are_not_written()
    {
        using (var p = Create()) p.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("Application started.");
        Assert.Empty(Lines());
    }

    [Fact]
    public void Scope_values_are_captured_when_Log_is_called()
    {
        // The scope is disposed before the writer thread dequeues; the record must still carry its cid.
        using (var p = Create())
        {
            var factory = LoggerFactory.Create(b => b.AddProvider(p));
            var log = factory.CreateLogger("x");
            using (log.BeginScope(new LogScope("c-11111111", "desktop_click", false, false, false)))
                LogEvent.Emit(log, LogLevel.Information, "tool.start");
        }
        Assert.Equal("c-11111111", JsonDocument.Parse(Assert.Single(Lines())).RootElement.GetProperty("cid").GetString());
    }

    [Fact]
    public void Rolls_to_a_new_segment_without_renaming_and_keeps_five()
    {
        using (var p = Create(segmentBytes: 400))
        {
            var log = p.CreateLogger("x");
            for (int i = 0; i < 60; i++) LogEvent.Emit(log, LogLevel.Information, "e", LogEvent.F("pad", new string('x', 100)));
        }
        var files = Directory.GetFiles(_dir, "*.jsonl").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(5, files.Length);
        Assert.All(files, f => Assert.Matches(@"^flaui-mcp-4242-20261107T100000Z-\d{2}\.jsonl$", f!));
        Assert.DoesNotContain("flaui-mcp-4242-20261107T100000Z-01.jsonl", files);   // oldest deleted
    }

    [Fact]
    public void A_segment_held_open_by_a_reader_does_not_stop_writing()
    {
        using var p = Create(segmentBytes: 400);
        var log = p.CreateLogger("x");
        LogEvent.Emit(log, LogLevel.Information, "e", LogEvent.F("pad", new string('x', 100)));
        p.FlushForTest();
        using var reader = new FileStream(p.CurrentFilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        for (int i = 0; i < 60; i++) LogEvent.Emit(log, LogLevel.Information, "e", LogEvent.F("pad", new string('x', 100)));
        p.FlushForTest();
        Assert.True(Directory.GetFiles(_dir, "*.jsonl").Length >= 5);
    }

    [Fact]
    public void Full_channel_drops_counts_and_reports_log_dropped()
    {
        using (var p = Create(capacity: 1))
        {
            p.PauseWriterForTest();
            var log = p.CreateLogger("x");
            for (int i = 0; i < 50; i++) LogEvent.Emit(log, LogLevel.Information, "e");
            Assert.True(p.DroppedTotal >= 40);
            p.ResumeWriterForTest();
            LogEvent.Emit(log, LogLevel.Information, "after");
        }
        Assert.Contains(Lines(), l => l.Contains("\"ev\":\"log.dropped\""));
    }

    [Fact]
    public void Prunes_dead_process_files_keeping_the_newest_twenty_and_never_a_live_one()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 25; i++)
        {
            var f = Path.Combine(_dir, $"flaui-mcp-{1000 + i}-20261101T000000Z-01.jsonl");
            File.WriteAllText(f, "{}\n");
            File.SetLastWriteTimeUtc(f, Start.AddMinutes(-100 + i));
        }
        var live = Path.Combine(_dir, "flaui-mcp-7777-20261101T000000Z-01.jsonl");
        File.WriteAllText(live, "{}\n");
        File.SetLastWriteTimeUtc(live, Start.AddDays(-30));
        JsonlFileLoggerProvider.PruneDead(_dir, keepDeadFiles: 20, isAlive: (pid, _) => pid == 7777);
        var left = Directory.GetFiles(_dir, "*.jsonl").Select(Path.GetFileName).ToArray();
        Assert.Contains("flaui-mcp-7777-20261101T000000Z-01.jsonl", left);
        Assert.Equal(21, left.Length);
        Assert.DoesNotContain("flaui-mcp-1000-20261101T000000Z-01.jsonl", left);   // oldest dead gone
    }

    [Fact]
    public void Shutdown_drains_and_the_stop_record_is_last()
    {
        using (var p = Create())
        {
            var log = p.CreateLogger("x");
            for (int i = 0; i < 200; i++) LogEvent.Emit(log, LogLevel.Information, "e");
            LogEvent.Emit(log, LogLevel.Information, "server.stop");
        }
        var lines = Lines();
        Assert.Equal(201, lines.Length);
        Assert.Contains("\"ev\":\"server.stop\"", lines[^1]);
    }

    [Fact]
    public void TryCreate_reports_an_unusable_directory_instead_of_throwing()
    {
        var file = Path.Combine(Path.GetTempPath(), "flaui-log-notadir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            var opts = new JsonlLogOptions(file, 1, Start, LogLevel.Information);
            var p = JsonlFileLoggerProvider.TryCreate(opts, new LogMasker(new ContentHasher(), _ => false), out var error);
            Assert.Null(p);
            Assert.False(string.IsNullOrEmpty(error));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Reader_rule_skips_only_a_torn_final_line()
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "flaui-mcp-1-20261101T000000Z-01.jsonl");
        File.WriteAllText(f, "{\"ev\":\"a\"}\n{\"ev\":\"b\"}\n{\"ev\":\"c");
        var evs = JsonlLogReader.ReadSegments(new[] { f }).Select(e => e.GetProperty("ev").GetString()).ToArray();
        Assert.Equal(new[] { "a", "b" }, evs);
    }
}
```

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: Implement** `JsonlFileLoggerProvider.cs`:

```csharp
// (license header)

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Server.Logging;

public sealed record JsonlLogOptions(string Directory, int Pid, DateTime ProcessStartUtc, LogLevel MinLevel)
{
    public long SegmentBytes { get; init; } = 5 * 1024 * 1024;
    public int KeepSegments { get; init; } = 5;
    public int ChannelCapacity { get; init; } = 4096;
    public Func<int, DateTime, bool> IsAlive { get; init; } = JsonlFileLoggerProvider.DefaultIsAlive;

    /// <summary>`FLAUI_MCP_LOG_DIR`, else `%LOCALAPPDATA%\flaui-mcp\logs`.</summary>
    public static string DefaultDirectory() =>
        Environment.GetEnvironmentVariable("FLAUI_MCP_LOG_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flaui-mcp", "logs");
}

/// <summary>Writes LogEventState records as JSON Lines (spec §4.1). Producers never block; one background
/// thread owns the file. Never writes to stdout.</summary>
public sealed class JsonlFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private static readonly Regex FileName = new(@"^flaui-mcp-(?<pid>\d+)-(?<start>\d{8}T\d{6}Z)-(?<seg>\d{2,})\.jsonl$");   // two digits minimum; a long run passes 99

    private readonly JsonlLogOptions _o;
    private readonly LogMasker _masker;
    private readonly Channel<LogRecord> _channel;
    private readonly Thread _writer;
    private readonly string _prefix;
    private IExternalScopeProvider? _scopes;
    private FileStream? _stream;
    private int _segment;
    private long _bytes;
    private long _dropped, _droppedReported, _droppedTotal;
    private DateTime _reopenAfter;
    private volatile string? _currentPath;
    private readonly ManualResetEventSlim _paused = new(true);
    private readonly ManualResetEventSlim _idle = new(true);

    private JsonlFileLoggerProvider(JsonlLogOptions o, LogMasker masker, FileStream first)
    {
        _o = o; _masker = masker; _stream = first; _segment = 1;
        _prefix = $"flaui-mcp-{o.Pid}-{o.ProcessStartUtc.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}-";
        _currentPath = first.Name;
        _bytes = first.Length;
        _channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(o.ChannelCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _writer = new Thread(WriterLoop) { Name = "flaui-log-writer", IsBackground = true };
        _writer.Start();
    }

    public string? CurrentFilePath => _currentPath;
    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    /// <summary>Creates the directory and opens segment 01. Returns null with an error message instead of
    /// throwing: a server whose log cannot be opened still starts (spec §7).</summary>
    public static JsonlFileLoggerProvider? TryCreate(JsonlLogOptions o, LogMasker masker, out string? error)
    {
        try
        {
            System.IO.Directory.CreateDirectory(o.Directory);
            var prefix = $"flaui-mcp-{o.Pid}-{o.ProcessStartUtc.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}-";
            var first = Open(Path.Combine(o.Directory, prefix + "01.jsonl"));
            error = null;
            return new JsonlFileLoggerProvider(o, masker, first);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"flaui-mcp: the log file could not be opened in '{o.Directory}': {ex.Message}. " +
                    "The server runs without a log file.";
            return null;
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    private void Enqueue(LogLevel level, LogEventState state)
    {
        if (level < _o.MinLevel) return;
        // Scope captured HERE, on the producer thread (spec §4.1): the writer thread does not share the
        // caller's AsyncLocal state, and the scope may be disposed before the record is dequeued.
        var scope = CapturedScope(_scopes);
        var fields = _masker.Apply(state.Event, state.Fields, scope);
        var record = new LogRecord(DateTime.UtcNow, level, state.Event, scope, fields);
        if (!_channel.Writer.TryWrite(record)) { Interlocked.Increment(ref _dropped); Interlocked.Increment(ref _droppedTotal); }
    }

    // ForEachScope runs outermost-first, so the innermost LogScope wins. A one-slot array is the state
    // object because the callback is static (no closure allocation per record).
    private static LogScope? CapturedScope(IExternalScopeProvider? scopes)
    {
        if (scopes is null) return null;
        var box = new LogScope?[1];
        scopes.ForEachScope(static (s, b) => { if (s is LogScope ls) b[0] = ls; }, box);
        return box[0];
    }

    private void WriterLoop()
    {
        var reader = _channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            _paused.Wait();
            _idle.Reset();
            while (reader.TryRead(out var record))
            {
                Write(JsonlRecordSerializer.Serialize(record, _o.Pid));
                var pending = Interlocked.Read(ref _dropped) - _droppedReported;
                if (pending > 0 && _stream is not null)
                {
                    _droppedReported += pending;
                    Write(JsonlRecordSerializer.Serialize(new LogRecord(DateTime.UtcNow, LogLevel.Warning, "log.dropped", null,
                        new[] { LogEvent.F("count", pending) }), _o.Pid));
                }
            }
            try { _stream?.Flush(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CloseAfterError(); }
            _idle.Set();
        }
        try { _stream?.Flush(); _stream?.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        _idle.Set();
    }

    private void Write(byte[] line)
    {
        if (_stream is null)
        {
            if (DateTime.UtcNow < _reopenAfter) { Interlocked.Increment(ref _droppedTotal); return; }
            try { _stream = Open(SegmentPath(_segment)); _bytes = _stream.Length; _currentPath = _stream.Name; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _reopenAfter = DateTime.UtcNow.AddSeconds(30); Interlocked.Increment(ref _droppedTotal); return; }
        }
        if (_bytes > 0 && _bytes + line.Length > _o.SegmentBytes) Roll();
        try { _stream!.Write(line, 0, line.Length); _bytes += line.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CloseAfterError(); Interlocked.Increment(ref _droppedTotal); }
    }

    private void Roll()
    {
        FileStream next;
        try { next = Open(SegmentPath(_segment + 1)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // keep appending; retry next write
        try { _stream!.Flush(); _stream.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        _stream = next; _segment++; _bytes = 0; _currentPath = next.Name;
        for (int s = _segment - _o.KeepSegments; s >= 1; s--)
        {
            var old = SegmentPath(s);
            if (!File.Exists(old)) continue;   // a gap (deleted earlier) must not stop retrying an older held-open one
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; } // held open: retry next roll
        }
    }

    private string SegmentPath(int seg) => Path.Combine(_o.Directory, _prefix + seg.ToString("00", CultureInfo.InvariantCulture) + ".jsonl");

    private void CloseAfterError()
    {
        try { _stream?.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        _stream = null; _reopenAfter = DateTime.UtcNow.AddSeconds(30);
    }

    /// <summary>Drains for up to 1 s, then closes. The caller logs `server.stop` BEFORE disposing, so it is
    /// already in the channel and is the last record written (spec §7).</summary>
    public void Dispose()
    {
        _paused.Set();
        _channel.Writer.TryComplete();
        _writer.Join(TimeSpan.FromSeconds(1));
    }

    /// <summary>Delete files of dead processes, keeping the newest `keepDeadFiles` of them (spec §4.1).</summary>
    public static void PruneDead(string dir, int keepDeadFiles, Func<int, DateTime, bool> isAlive)
    {
        if (!System.IO.Directory.Exists(dir)) return;
        var dead = new List<FileInfo>();
        var alive = new Dictionary<(int, DateTime), bool>();
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "flaui-mcp-*.jsonl"))
        {
            var m = FileName.Match(Path.GetFileName(path));
            if (!m.Success) continue;
            var pid = int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture);
            var start = DateTime.ParseExact(m.Groups["start"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (!alive.TryGetValue((pid, start), out var a)) alive[(pid, start)] = a = isAlive(pid, start);
            if (!a) dead.Add(new FileInfo(path));
        }
        foreach (var f in dead.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keepDeadFiles))
            try { f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
    }

    /// <summary>Alive = a process with that pid exists AND started within 2 s of the recorded time (pids are
    /// reused). Unknown (access denied) counts as alive: never delete what we cannot prove is dead.</summary>
    public static bool DefaultIsAlive(int pid, DateTime startUtc)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return true; }
        using (p)
        {
            try { return Math.Abs((p.StartTime.ToUniversalTime() - startUtc).TotalSeconds) < 2; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return true; }
        }
    }

    // ---- test hooks ----
    internal void PauseWriterForTest() => _paused.Reset();
    internal void ResumeWriterForTest() => _paused.Set();
    internal void FlushForTest()
    {
        for (int i = 0; i < 200 && (_channel.Reader.Count > 0 || !_idle.IsSet); i++) Thread.Sleep(10);
    }

    private sealed class Logger(JsonlFileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; // scopes come via ISupportExternalScope
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._o.MinLevel && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is LogEventState s) owner.Enqueue(logLevel, s);
        }
    }
}

/// <summary>Implements the reader rule (spec §5.3): segments in order, a torn FINAL line of the newest
/// segment is skipped. Used by tests and documented for agents.</summary>
public static class JsonlLogReader
{
    public static IEnumerable<JsonElement> ReadSegments(IReadOnlyList<string> segmentsInOrder)
    {
        for (int s = 0; s < segmentsInOrder.Count; s++)
        {
            string[] lines;
            using (var fs = new FileStream(segmentsInOrder[s], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) lines = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                JsonElement e;
                try { e = JsonDocument.Parse(lines[i]).RootElement.Clone(); }
                catch (JsonException) when (s == segmentsInOrder.Count - 1 && i == lines.Length - 1) { yield break; }
                yield return e;
            }
        }
    }
}
```

- [ ] **Step 4:** Build; run `--filter "FullyQualifiedName~JsonlFileLoggerProviderTests"` → 10 passed. Run it 3 times (thread timing): all green each time.
- [ ] **Step 5: Mutation check.** In `Enqueue`, replace `var scope = CapturedScope(_scopes);` with `LogScope? scope = null;` → `Scope_values_are_captured_when_Log_is_called` FAILS. Restore.
- [ ] **Step 6:** Commit `feat(server): JSONL file logger provider (segments, pruning, drop accounting)`.

---

### Task 6: Parameter catalog and argument classifier

**Files:** Modify `src/FlaUI.Mcp.Core/Perception/Selector.cs:16-22`; Create `src/FlaUI.Mcp.Server/Logging/ToolParameterCatalog.cs`, `ArgumentClassifier.cs`; `test/FlaUI.Mcp.Tests/Logging/ArgumentClassifierTests.cs`.

- [ ] **Step 1: Annotate `Selector`.** Add `using FlaUI.Mcp.Core.Logging;` and change the record header (lines 16-22) to:

```csharp
public sealed record Selector(
    [property: LogUiText] string? AutomationId = null,
    [property: LogUiText] string? Name = null,
    [property: LogStructural] string NameMatch = "eq",
    [property: LogStructural] string? ControlType = null,
    [property: LogStructural] string? Scope = null,
    bool IgnoreCase = true)
```

- [ ] **Step 2: Failing tests** `ArgumentClassifierTests.cs`. They use a private fake tool type so they do not depend on Task 7's annotations:

```csharp
// (license header)

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Server.Logging;
using ModelContextProtocol.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

[McpServerToolType]
public sealed class FakeTools
{
    [McpServerTool] public string DesktopFakeType([LogStructural] string window, string text, [LogStructural] string? @ref = null,
        Selector? selector = null, int timeoutMs = 4000, bool verify = true) => "";
    [McpServerTool, LogForegroundTarget] public string DesktopFakeKey([LogStructural] string chord, [LogStructural] string? window = null) => "";
    [McpServerTool] public string DesktopFakeFind([LogStructural] string window, [LogUiText] string? name = null,
        [LogStructural] int textLength = 0, [LogStructural] string[]? modifiers = null) => "";
}

public class ArgumentClassifierTests
{
    private static readonly ToolParameterCatalog Catalog = ToolParameterCatalog.FromTypes(new[] { typeof(FakeTools) });
    private static readonly ContentHasher Hasher = new(new byte[32]);

    private static IDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static (JsonObject Args, bool Content) Run(string tool, string json, bool sensitive = false) =>
        ArgumentClassifier.Classify(Catalog.Find(tool), Args(json), sensitive, Hasher);

    private static bool IsMasked(JsonNode? n) => n is JsonObject o && o.ContainsKey("len") && o.ContainsKey("sha8");

    [Fact]
    public void Names_are_snake_case_of_the_method()
    {
        Assert.NotNull(Catalog.Find("desktop_fake_type"));
        Assert.True(Catalog.Find("desktop_fake_key")!.ForegroundTarget);
        Assert.False(Catalog.Find("desktop_fake_type")!.ForegroundTarget);
    }

    [Fact]
    public void Structural_raw_content_hashed_numbers_raw()
    {
        var (a, content) = Run("desktop_fake_type", """{"window":"w3","text":"hunter2","ref":"e5","timeoutMs":250,"verify":false}""");
        Assert.Equal("w3", a["window"]!.GetValue<string>());
        Assert.Equal("e5", a["ref"]!.GetValue<string>());
        Assert.True(IsMasked(a["text"]));
        Assert.Equal(7, a["text"]!["len"]!.GetValue<int>());
        Assert.Equal(250, a["timeoutMs"]!.GetValue<int>());
        Assert.False(a["verify"]!.GetValue<bool>());
        Assert.True(content);
    }

    [Fact]
    public void Ui_text_raw_unless_sensitive()
    {
        Assert.Equal("Save As", Run("desktop_fake_find", """{"window":"w1","name":"Save As"}""").Args["name"]!.GetValue<string>());
        Assert.True(IsMasked(Run("desktop_fake_find", """{"window":"w1","name":"Save As"}""", sensitive: true).Args["name"]));
        Assert.False(Run("desktop_fake_find", """{"window":"w1","name":"Save As"}""").Content);
    }

    [Fact]
    public void Selector_is_classified_per_property()
    {
        var json = """{"window":"w1","text":"x","selector":{"name":"OK","automationId":"btnOk","controlType":"Button","nameMatch":"eq","ignoreCase":true,"extra":"zzz"}}""";
        var sel = Run("desktop_fake_type", json).Args["selector"]!.AsObject();
        Assert.Equal("OK", sel["name"]!.GetValue<string>());
        Assert.Equal("btnOk", sel["automationId"]!.GetValue<string>());
        Assert.Equal("Button", sel["controlType"]!.GetValue<string>());
        Assert.True(sel["ignoreCase"]!.GetValue<bool>());
        Assert.True(IsMasked(sel["extra"]));                       // unknown property: content
        var masked = Run("desktop_fake_type", json, sensitive: true).Args["selector"]!.AsObject();
        Assert.True(IsMasked(masked["name"]));
        Assert.Equal("Button", masked["controlType"]!.GetValue<string>());
    }

    [Fact]
    public void Classification_is_by_attribute_never_by_name()
    {
        // Distractor: a structural parameter whose NAME looks like content stays raw.
        Assert.Equal(12, Run("desktop_fake_find", """{"window":"w1","textLength":12}""").Args["textLength"]!.GetValue<int>());
        // A string sent to a numeric parameter is not trusted as structural.
        Assert.True(IsMasked(Run("desktop_fake_find", """{"window":"w1","textLength":"secret"}""").Args["textLength"]));
    }

    [Fact]
    public void Unknown_tool_and_unknown_param_fail_closed()
    {
        var (a, content) = ArgumentClassifier.Classify(null, Args("""{"window":"w1","q":"x","n":3}"""), false, Hasher);
        Assert.True(IsMasked(a["window"]));
        Assert.True(IsMasked(a["q"]));
        Assert.Equal(3, a["n"]!.GetValue<int>());
        Assert.True(content);
        Assert.True(IsMasked(Run("desktop_fake_type", """{"window":"w1","bogus":"x"}""").Args["bogus"]));
    }

    [Fact]
    public void Structural_string_array_raw()
    {
        var a = Run("desktop_fake_find", """{"window":"w1","modifiers":["Ctrl","Shift"]}""").Args;
        Assert.Equal("Ctrl", a["modifiers"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Null_and_empty_content_do_not_count_as_content()
    {
        Assert.False(Run("desktop_fake_type", """{"window":"w1","text":null}""").Content);
    }
}
```

- [ ] **Step 3:** Build → FAIL.
- [ ] **Step 4: Implement** `ToolParameterCatalog.cs`:

```csharp
// (license header)

using System.Reflection;
using System.Text;
using FlaUI.Mcp.Core.Logging;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Logging;

public enum ParamClass { Structural, UiText, Content, Number, Complex }

public sealed record ParamInfo(string Name, ParamClass Class, IReadOnlyDictionary<string, ParamClass>? Properties = null);

public sealed record ToolEntry(string Name, bool ForegroundTarget, IReadOnlyDictionary<string, ParamInfo> Parameters);

/// <summary>Tool name → parameter classes, read once by reflection from the [McpServerTool] methods
/// (spec §6.1). Names are the snake_case of the method name, which is what the SDK registers (no tool in
/// this repo sets `Name =`).</summary>
public sealed class ToolParameterCatalog
{
    private readonly Dictionary<string, ToolEntry> _tools;
    private ToolParameterCatalog(Dictionary<string, ToolEntry> tools) => _tools = tools;

    public IReadOnlyCollection<ToolEntry> Tools => _tools.Values;
    public ToolEntry? Find(string toolName) => _tools.TryGetValue(toolName, out var t) ? t : null;

    public static ToolParameterCatalog FromAssembly(Assembly assembly) =>
        FromTypes(assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null));

    public static ToolParameterCatalog FromTypes(IEnumerable<Type> types)
    {
        var map = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);
        foreach (var type in types)
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
            var ps = new Dictionary<string, ParamInfo>(StringComparer.Ordinal);
            foreach (var p in m.GetParameters())
            {
                if (p.Name is null || p.ParameterType == typeof(McpServer)) continue;
                ps[p.Name] = Describe(p.Name, p.ParameterType, p);
            }
            var name = SnakeCase(m.Name);
            map[name] = new ToolEntry(name, m.GetCustomAttribute<LogForegroundTargetAttribute>() is not null, ps);
        }
        return new ToolParameterCatalog(map);
    }

    internal static ParamClass ClassOf(ICustomAttributeProvider member) =>
        member.IsDefined(typeof(LogStructuralAttribute), false) ? ParamClass.Structural
        : member.IsDefined(typeof(LogUiTextAttribute), false) ? ParamClass.UiText
        : ParamClass.Content;

    private static ParamInfo Describe(string name, Type type, ICustomAttributeProvider attrs)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string) || t == typeof(string[])) return new ParamInfo(name, ClassOf(attrs));
        if (t.IsPrimitive || t == typeof(decimal) || t == typeof(double[]) || t == typeof(int[])) return new ParamInfo(name, ParamClass.Number);
        // Complex record bound from a JSON object: classify per property (spec §6.1).
        var props = new Dictionary<string, ParamClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            props[prop.Name] = pt == typeof(string) ? ClassOf(prop) : pt.IsPrimitive ? ParamClass.Number : ParamClass.Content;
        }
        return new ParamInfo(name, ParamClass.Complex, props);
    }

    public static string SnakeCase(string methodName)
    {
        var n = methodName.EndsWith("Async", StringComparison.Ordinal) ? methodName[..^5] : methodName;
        var sb = new StringBuilder(n.Length + 8);
        for (int i = 0; i < n.Length; i++)
        {
            if (char.IsUpper(n[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(n[i]));
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 5: Implement** `ArgumentClassifier.cs`:

```csharp
// (license header)

using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Builds the logged `args` object (spec §6). Fails closed: an unknown tool, an unknown parameter,
/// an unknown property, or a string where a number was declared is content.</summary>
public static class ArgumentClassifier
{
    public static (JsonObject Args, bool HasContentArgs) Classify(ToolEntry? tool, IDictionary<string, JsonElement>? args,
        bool sensitive, ContentHasher hasher)
    {
        var o = new JsonObject();
        bool content = false;
        if (args is null) return (o, false);
        foreach (var (key, value) in args)
        {
            ParamInfo? p = null;
            tool?.Parameters.TryGetValue(key, out p);
            o[key] = p is null ? Walk(value, ParamClass.Content, sensitive, hasher, ref content)
                   : p.Class == ParamClass.Complex ? Complex(value, p, sensitive, hasher, ref content)
                   : Walk(value, p.Class, sensitive, hasher, ref content);
        }
        return (o, content);
    }

    private static JsonNode? Complex(JsonElement v, ParamInfo p, bool sensitive, ContentHasher hasher, ref bool content)
    {
        if (v.ValueKind != JsonValueKind.Object) return Walk(v, ParamClass.Content, sensitive, hasher, ref content);
        var o = new JsonObject();
        foreach (var prop in v.EnumerateObject())
        {
            var cls = p.Properties!.TryGetValue(prop.Name, out var c) ? c : ParamClass.Content;
            o[prop.Name] = Walk(prop.Value, cls, sensitive, hasher, ref content);
        }
        return o;
    }

    private static JsonNode? Walk(JsonElement v, ParamClass cls, bool sensitive, ContentHasher hasher, ref bool content)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString()!;
                switch (cls)
                {
                    case ParamClass.Structural: return JsonValue.Create(s);
                    case ParamClass.UiText: return sensitive ? Masked(hasher.Mask(s)) : JsonValue.Create(s);
                    default:
                        if (s.Length > 0) content = true;
                        return Masked(hasher.Mask(s));
                }
            case JsonValueKind.Number: return JsonNode.Parse(v.GetRawText());
            case JsonValueKind.True: return JsonValue.Create(true);
            case JsonValueKind.False: return JsonValue.Create(false);
            case JsonValueKind.Null: return null;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var item in v.EnumerateArray()) a.Add(Walk(item, cls, sensitive, hasher, ref content));
                return a;
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var prop in v.EnumerateObject()) o[prop.Name] = Walk(prop.Value, ParamClass.Content, sensitive, hasher, ref content);
                return o;
            default: return null;
        }
    }

    private static JsonObject Masked(MaskedValue m) => new() { ["len"] = m.Len, ["sha8"] = m.Sha8 };
}
```

- [ ] **Step 6:** Build; run `--filter "FullyQualifiedName~ArgumentClassifierTests"` → 8 passed.
- [ ] **Step 7: Mutation check.** In `Walk`, change `case ParamClass.UiText: return sensitive ? ...` to `return JsonValue.Create(s);` → `Ui_text_raw_unless_sensitive` and `Selector_is_classified_per_property` FAIL. Restore.
- [ ] **Step 8:** Commit `feat(server): tool parameter catalog and fail-closed argument classifier`.

---

### Task 7: Annotate every tool parameter + coverage guard

**Files:** Modify the 13 files in `src/FlaUI.Mcp.Server/Tools/` that contain `[McpServerToolType]`; Create `test/FlaUI.Mcp.Tests/Logging/ToolParameterCoverageTests.cs`.

**The oracle is the test below.** It is complete; implement until it passes. Do NOT edit its tables to make the code pass: if a parameter seems misclassified, stop and report `[param] -> [class you think] because <reason>`.

- [ ] **Step 1: Write the test** (it fails now: nothing is annotated):

```csharp
// (license header)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spec §6.2. Every string parameter of every tool, and every string property of a complex
/// parameter type, is classified on purpose — in BOTH directions.</summary>
public class ToolParameterCoverageTests
{
    // Parameter NAMES that are structural in every tool that has them.
    private static readonly HashSet<string> StructuralNames = new(StringComparer.Ordinal)
    {
        "window", "ref", "root", "scope", "scopeRef", "endWindow", "baselineSnapshotId", "snapshotId",
        "subscriptionId", "wakeId", "direction", "action", "button", "modifiers", "chord", "events",
        "nameMatch", "controlType", "matchMode", "output", "until", "by", "path",
    };

    // UI text, as tool/param.
    private static readonly HashSet<string> UiText = new(StringComparer.Ordinal)
    {
        "desktop_find/automationId", "desktop_find/name",
        "desktop_find_text/query", "desktop_wait_for_text/query",
        "desktop_tray_action/icon", "desktop_tray_action/menuItem",
        "desktop_open_window/value", "desktop_wait_for/value", "desktop_wait_for_stable/value",
    };

    // Content on purpose (hashed): carry NEITHER attribute.
    private static readonly HashSet<string> Content = new(StringComparer.Ordinal)
    {
        "desktop_type/text", "desktop_paste_text/text", "desktop_clipboard_set/text",
        "desktop_set_value/value", "desktop_launch_app/args", "desktop_wait_for/equals",
    };

    private static IEnumerable<(string Tool, ParameterInfo P)> StringParams()
    {
        foreach (var t in typeof(ToolResponse).Assembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is not null))
        foreach (var m in t.GetMethods().Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null))
        foreach (var p in m.GetParameters())
        {
            var pt = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            if (pt == typeof(string) || pt == typeof(string[])) yield return (ToolParameterCatalog.SnakeCase(m.Name), p);
        }
    }

    [Fact]
    public void The_catalog_sees_all_53_tools_with_desktop_names()
    {
        var c = ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly);
        Assert.Equal(53, c.Tools.Count);
        Assert.All(c.Tools, t => Assert.Matches("^desktop_[a-z]+(_[a-z]+)*$", t.Name));
        Assert.NotNull(c.Find("desktop_get_text"));
        Assert.NotNull(c.Find("desktop_type"));
    }

    [Fact]
    public void Every_string_parameter_is_classified_exactly_as_the_oracle_says()
    {
        var problems = new List<string>();
        foreach (var (tool, p) in StringParams())
        {
            var key = $"{tool}/{p.Name}";
            var actual = ToolParameterCatalog.ClassOf(p);
            ParamClass expected =
                Content.Contains(key) ? ParamClass.Content
                : UiText.Contains(key) ? ParamClass.UiText
                : StructuralNames.Contains(p.Name!) ? ParamClass.Structural
                : (ParamClass)(-1);
            if ((int)expected == -1) problems.Add($"{key}: not in the oracle - classify it in this test on purpose");
            else if (actual != expected) problems.Add($"{key}: is {actual}, oracle says {expected}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Content_parameters_carry_neither_attribute()
    {
        // Reverse direction: marking a content parameter raw to silence the guard must fail.
        var found = StringParams().Where(x => Content.Contains($"{x.Tool}/{x.P.Name}")).ToList();
        Assert.Equal(Content.Count, found.Count);
        Assert.All(found, x =>
        {
            Assert.False(x.P.IsDefined(typeof(LogStructuralAttribute), false), $"{x.Tool}/{x.P.Name}");
            Assert.False(x.P.IsDefined(typeof(LogUiTextAttribute), false), $"{x.Tool}/{x.P.Name}");
        });
    }

    [Fact]
    public void Every_string_property_of_a_complex_parameter_type_is_classified()
    {
        var expected = new Dictionary<string, ParamClass>
        {
            ["AutomationId"] = ParamClass.UiText, ["Name"] = ParamClass.UiText,
            ["NameMatch"] = ParamClass.Structural, ["ControlType"] = ParamClass.Structural, ["Scope"] = ParamClass.Structural,
        };
        var complexTypes = typeof(ToolResponse).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null)
            .SelectMany(m => m.GetParameters())
            .Select(p => Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType)
            .Where(t => !t.IsPrimitive && t != typeof(string) && !t.IsArray && t != typeof(ModelContextProtocol.Server.McpServer))
            .Distinct().ToList();
        Assert.Equal(new[] { typeof(FlaUI.Mcp.Core.Perception.Selector) }, complexTypes);
        foreach (var prop in typeof(FlaUI.Mcp.Core.Perception.Selector).GetProperties().Where(p => p.PropertyType == typeof(string)))
            Assert.Equal(expected[prop.Name], ToolParameterCatalog.ClassOf(prop));
    }

    [Fact]
    public void Only_desktop_key_is_a_foreground_target()
    {
        var c = ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly);
        Assert.Equal(new[] { "desktop_key" }, c.Tools.Where(t => t.ForegroundTarget).Select(t => t.Name).ToArray());
    }
}
```

- [ ] **Step 2:** Build, run `--filter "FullyQualifiedName~ToolParameterCoverageTests"` → `Every_string_parameter...`, `Only_desktop_key...` FAIL; the failure message lists every unannotated parameter.
- [ ] **Step 3: Annotate.** In each of the 13 tool files add `using FlaUI.Mcp.Core.Logging;`. Then, for every `string`/`string?`/`string[]`/`string[]?` parameter of every `[McpServerTool]` method:
  - name in `StructuralNames` and the `tool/param` key NOT in `UiText` or `Content` → put `[LogStructural]` before its `[Description(...)]`;
  - key in `UiText` → `[LogUiText]`;
  - key in `Content` → nothing.
  - Add `[LogForegroundTarget]` to `DesktopKey` (`InputTools.cs:336`), on its own line between `[McpServerTool(...), Description(...)]` and the signature.
  - Example (`InteractionTools.cs:86-94`, `desktop_set_value`): `window` gets `[LogStructural]`, `value` gets nothing, `@ref` gets `[LogStructural]`. A parameter written as `[Description("...")] string window` becomes `[LogStructural, Description("...")] string window`.
  - Shared constants: if a parameter's `[Description]` is built from a constant (e.g. `SelectorGating.SelectorDesc`), that does not matter here — the attribute goes on the parameter. `Selector? selector` parameters are NOT strings: they get no attribute (the type is annotated in Task 6).
- [ ] **Step 4:** Build; run the coverage tests → 5 passed. Then the headless gate → baseline + tests added so far, 0 failed. (Tool wire schemas are unchanged: these attributes are not `[Description]`.)
- [ ] **Step 5: Mutation check.** Remove `[LogStructural]` from `desktop_type`'s `window` → `Every_string_parameter...` FAILS naming `desktop_type/window`. Add `[LogStructural]` to `desktop_type`'s `text` → `Content_parameters_carry_neither_attribute` FAILS. Restore both.
- [ ] **Step 6:** Commit `feat(server): classify every tool parameter for logging (coverage guard)`.

---

### Task 8: Call sensitivity and the tool-call filter

**Files:** Create `src/FlaUI.Mcp.Server/Logging/CallSensitivity.cs`, `ToolCallLogFilter.cs`; `test/FlaUI.Mcp.Tests/Logging/ToolCallLogFilterTests.cs`.

- [ ] **Step 1: Failing tests:**

```csharp
// (license header)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class ToolCallLogFilterTests
{
    private static readonly ToolParameterCatalog Catalog = ToolParameterCatalog.FromTypes(new[] { typeof(FakeTools) });

    private sealed class FixedSensitivity(bool sensitive, bool targetless = false) : ICallSensitivity
    {
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) => (sensitive, targetless);
    }

    private static (ToolCallLogFilter Filter, CapturingLogger Log) Make(bool sensitive = false, bool targetless = false)
    {
        var log = new CapturingLogger();
        return (new ToolCallLogFilter(log, Catalog, new FixedSensitivity(sensitive, targetless), new ContentHasher(new byte[32])), log);
    }

    private static IDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static CallToolResult Text(string s) => new() { Content = new List<ContentBlock> { new TextContentBlock { Text = s } } };

    [Fact]
    public async Task Emits_start_and_end_sharing_a_cid_and_returns_the_result_unchanged()
    {
        var (f, log) = Make();
        var expected = Text("""{"ok":true}""");
        LogScope? seen = null;
        var r = await f.RunAsync("desktop_fake_type", Args("""{"window":"w1","text":"abc"}"""),
            () => { seen = LogContext.Current; return new ValueTask<CallToolResult>(expected); });
        Assert.Same(expected, r);
        var start = log.Single("tool.start");
        var end = log.Single("tool.end");
        Assert.Equal("ok", end.Fields.Single(x => x.Key == "outcome").Value);
        Assert.NotNull(seen);
        Assert.Matches("^c-[0-9a-f]{8}$", seen!.Cid);
        Assert.True(seen.HasContentArgs);
        Assert.Null(LogContext.Current);              // restored after the call
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task Error_json_result_is_read_as_the_outcome_with_its_message()
    {
        var (f, log) = Make();
        await f.RunAsync("desktop_fake_find", Args("""{"window":"w1"}"""),
            () => new ValueTask<CallToolResult>(Text("""{"error":"SelectorNoMatch","message":"no element","suggestedRecovery":null}""")));
        var end = log.Single("tool.end");
        Assert.Equal("SelectorNoMatch", end.Fields.Single(x => x.Key == "outcome").Value);
        var msg = end.Fields.Single(x => x.Key == "msg");
        Assert.Equal("no element", msg.Value);
        Assert.True(msg.UiText);
    }

    [Fact]
    public async Task Ambiguous_match_on_a_targetless_call_omits_msg()
    {
        var (f, log) = Make(targetless: true);
        await f.RunAsync("desktop_fake_find", Args("{}"),
            () => new ValueTask<CallToolResult>(Text("""{"error":"AmbiguousMatch","message":"2 windows titled 'KeePass': ...","suggestedRecovery":"x"}""")));
        Assert.DoesNotContain(log.Single("tool.end").Fields, x => x.Key == "msg");
    }

    [Fact]
    public async Task A_throwing_next_logs_INTERNAL_and_rethrows()
    {
        var (f, log) = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await f.RunAsync("desktop_fake_find", Args("{}"), () => throw new InvalidOperationException("x")));
        Assert.Equal("INTERNAL", log.Single("tool.end").Fields.Single(x => x.Key == "outcome").Value);
    }

    [Fact]
    public async Task A_failing_classifier_never_blocks_the_call()
    {
        var log = new CapturingLogger();
        var f = new ToolCallLogFilter(log, Catalog, new ThrowingSensitivity(), new ContentHasher());
        var expected = Text("{}");
        Assert.Same(expected, await f.RunAsync("desktop_fake_find", Args("{}"), () => new ValueTask<CallToolResult>(expected)));
    }

    private sealed class ThrowingSensitivity : ICallSensitivity
    {
        // Tuple element names must match the interface's (CS8141).
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) =>
            throw new InvalidOperationException();
    }
}
```

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: Implement** `CallSensitivity.cs`:

```csharp
// (license header)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Logging;

public interface ICallSensitivity
{
    (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args);
}

/// <summary>Spec §6.3. Pure Win32 + in-memory lookups only: never queues behind UIA work.</summary>
public sealed class CallSensitivity : ICallSensitivity
{
    private readonly WindowManager _windows;
    private readonly SensitivityClassifier _classifier;
    private readonly bool _rulesLoaded;

    public CallSensitivity(WindowManager windows, SensitivityClassifier classifier, ServerOptions options)
    { _windows = windows; _classifier = classifier; _rulesLoaded = options.RedactionRules is not null; }

    /// <summary>The window-level predicate: denylisted (null/empty process counts as denied,
    /// PerceptionPolicy.cs:54-55) or a process an operator rule could match.</summary>
    public bool IsSensitiveProcess(string? process) =>
        PerceptionPolicy.IsDenied(process) || _classifier.CouldMatchProcess(process);

    public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args)
    {
        if (args is not null && args.TryGetValue("window", out var w) && w.ValueKind == JsonValueKind.String
            && w.GetString() is { Length: > 0 } id)
            return (IsSensitiveProcess(_windows.TryGetAppName(new WindowHandle(id))), false);
        if (tool?.ForegroundTarget == true)
            return (IsSensitiveProcess(ForegroundProcess()), false);
        return (_rulesLoaded, true);
    }

    private static string? ForegroundProcess()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return null;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
```

- [ ] **Step 4: Implement** `ToolCallLogFilter.cs`:

```csharp
// (license header)

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Spec §4.2: one `tool.start` and one `tool.end` per call, inside a scope carrying the cid.</summary>
public sealed class ToolCallLogFilter
{
    private readonly ILogger _log;
    private readonly ToolParameterCatalog _catalog;
    private readonly ICallSensitivity _sensitivity;
    private readonly ContentHasher _hasher;
    private long _calls;

    public ToolCallLogFilter(ILogger log, ToolParameterCatalog catalog, ICallSensitivity sensitivity, ContentHasher hasher)
    { _log = log; _catalog = catalog; _sensitivity = sensitivity; _hasher = hasher; }

    public long Calls => Interlocked.Read(ref _calls);

    public async ValueTask<CallToolResult> RunAsync(string toolName, IDictionary<string, JsonElement>? args,
        Func<ValueTask<CallToolResult>> next)
    {
        LogScope? scope = null;
        System.Text.Json.Nodes.JsonObject? logged = null;
        try
        {
            var tool = _catalog.Find(toolName);
            var (sensitive, targetless) = _sensitivity.Decide(tool, args);
            var (a, content) = ArgumentClassifier.Classify(tool, args, sensitive, _hasher);
            logged = a;
            scope = new LogScope("c-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
                toolName, sensitive, content, targetless);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; scope = null; }
        if (scope is null) return await next();

        Interlocked.Increment(ref _calls);
        var previous = LogContext.Current;
        LogContext.Current = scope;
        try
        {
            using var _ = SafeScope(scope);
            LogEvent.Emit(_log, LogLevel.Information, "tool.start", LogEvent.F("args", logged));
            var sw = Stopwatch.StartNew();
            CallToolResult result;
            try { result = await next(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                End(scope, "INTERNAL", ex.Message, sw.ElapsedMilliseconds);
                throw;
            }
            var (outcome, message) = SafeOutcome(result);
            End(scope, outcome, message, sw.ElapsedMilliseconds);
            return result;
        }
        finally { LogContext.Current = previous; }
    }

    private IDisposable? SafeScope(LogScope scope)
    {
        try { return _log.BeginScope(scope); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return null; }
    }

    private void End(LogScope scope, string outcome, string? message, long ms)
    {
        var fields = new List<LogField> { LogEvent.F("outcome", outcome), LogEvent.F("ms", ms) };
        bool omitMsg = scope.Targetless && outcome == "AmbiguousMatch";   // spec §6.3 exception
        if (outcome != "ok" && message is not null && !omitMsg) fields.Add(LogEvent.Ui("msg", message));
        LogEvent.Emit(_log, outcome == "ok" ? LogLevel.Information : LogLevel.Warning, "tool.end", fields.ToArray());
    }

    /// <summary>ToolResponse writes errors as a text block that STARTS with `{"error":` (its anonymous DTO
    /// puts `error` first). Only those are parsed, so an ok snapshot of megabytes is never re-parsed.</summary>
    private static (string Outcome, string? Message) SafeOutcome(CallToolResult result)
    {
        try
        {
            foreach (var block in result.Content ?? (IList<ContentBlock>)Array.Empty<ContentBlock>())
            {
                if (block is not TextContentBlock { Text: { } text } || !text.StartsWith("{\"error\":", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(text);
                var code = doc.RootElement.GetProperty("error").GetString() ?? "INTERNAL";
                var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (code, msg);
            }
            return result.IsError == true ? ("INTERNAL", null) : ("ok", null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return ("ok", null); }
    }
}
```

- [ ] **Step 5:** Build; run `--filter "FullyQualifiedName~ToolCallLogFilterTests"` → 5 passed.
- [ ] **Step 6:** Commit `feat(server): tool-call log filter with per-call scope`.

---

### Task 9: `tool.exception` and the `log` error pointer

**Files:** Create `src/FlaUI.Mcp.Server/Logging/ToolLog.cs`; Modify `src/FlaUI.Mcp.Server/Tools/ToolResponse.cs` (CRLF); Create `test/FlaUI.Mcp.Tests/Logging/ToolResponseLoggingTests.cs`.

**Oracle:** `test/FlaUI.Mcp.Tests/Server/ToolResponseCriticalTests.cs` must stay green unchanged (it pins the error JSON and the critical-exception filter).

- [ ] **Step 1: Failing tests:**

```csharp
// (license header)

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class ToolResponseLoggingTests
{
    [Fact]
    public async Task Internal_failure_logs_type_and_stack_and_the_error_carries_log()
    {
        var log = new CapturingLogger();
        using (ToolLog.Override(log, () => "c-12345678 in C:\\logs\\f.jsonl"))
        {
            var json = await ToolResponse.Guard(() => throw new InvalidOperationException("boom"));
            var root = JsonDocument.Parse(json).RootElement;
            Assert.Equal("INTERNAL", root.GetProperty("error").GetString());
            Assert.Equal("c-12345678 in C:\\logs\\f.jsonl", root.GetProperty("log").GetString());
        }
        var ev = log.Single("tool.exception");
        Assert.Equal("System.InvalidOperationException", ev.Fields.Single(f => f.Key == "exType").Value);
        Assert.False(string.IsNullOrEmpty(ev.Fields.Single(f => f.Key == "stack").Value as string));
        Assert.True(ev.Fields.Single(f => f.Key == "msg").UiText);
    }

    [Fact]
    public async Task ToolException_logs_no_exception_record_but_still_carries_log()
    {
        var log = new CapturingLogger();
        using (ToolLog.Override(log, () => "c-1 in x"))
        {
            var json = await ToolResponse.Guard(() => throw new ToolException(ToolErrorCode.SelectorNoMatch, "none", "retry"));
            Assert.Equal("c-1 in x", JsonDocument.Parse(json).RootElement.GetProperty("log").GetString());
        }
        Assert.Empty(log.Events);
    }

    [Fact]
    public async Task Without_a_pointer_there_is_no_log_key_at_all()
    {
        using (ToolLog.Override(null, () => null))
        {
            var json = await ToolResponse.Guard(() => throw new ToolException(ToolErrorCode.SelectorNoMatch, "none", "retry"));
            Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("log", out _));
            Assert.Equal("""{"error":"SelectorNoMatch","message":"none","suggestedRecovery":"retry"}""", json);
        }
    }

    [Fact]
    public async Task GuardImage_error_carries_log()
    {
        using (ToolLog.Override(new CapturingLogger(), () => "c-2 in y"))
        {
            var r = await ToolResponse.GuardImage(() => throw new InvalidOperationException("x"));
            var text = ((ModelContextProtocol.Protocol.TextContentBlock)r.Content.Single()).Text;
            Assert.Equal("c-2 in y", JsonDocument.Parse(text).RootElement.GetProperty("log").GetString());
        }
    }

    [Fact]
    public async Task OutOfMemory_still_propagates()
    {
        using (ToolLog.Override(new CapturingLogger(), () => "c in x"))
            await Assert.ThrowsAsync<OutOfMemoryException>(() => ToolResponse.Guard(() => throw new OutOfMemoryException()));
    }
}
```

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: Implement** `ToolLog.cs`:

```csharp
// (license header)

using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Logger + error pointer for the static ToolResponse helpers (spec §4.5). Configured once by
/// Program.cs; tests override per async flow so parallel tests never share the static.</summary>
public static class ToolLog
{
    private static ILogger? s_logger;
    private static Func<string?>? s_pointer;
    private static readonly AsyncLocal<Holder?> s_override = new();
    private sealed record Holder(ILogger? Logger, Func<string?>? Pointer);

    public static void Configure(ILogger? logger, Func<string?>? pointer) { s_logger = logger; s_pointer = pointer; }

    public static IDisposable Override(ILogger? logger, Func<string?>? pointer)
    {
        var previous = s_override.Value;
        s_override.Value = new Holder(logger, pointer);
        return new Restore(previous);
    }

    public static ILogger? Logger => s_override.Value is { } o ? o.Logger : s_logger;

    /// <summary>`"<cid> in <path>"`, or null when logging is off or no call is in scope.</summary>
    public static string? Pointer()
    {
        try { return (s_override.Value is { } o ? o.Pointer : s_pointer)?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return null; }
    }

    private sealed class Restore(Holder? previous) : IDisposable
    { public void Dispose() => s_override.Value = previous; }
}
```

- [ ] **Step 4: Modify `ToolResponse.cs`.** Add `using FlaUI.Mcp.Core.Logging;`, `using FlaUI.Mcp.Server.Logging;` and `using Microsoft.Extensions.Logging;`. Replace the body of `Guard` (lines 22-49), `GuardImage` (51-58) and `ErrResult` (60-61) so that the comments stay and the code becomes:

```csharp
    public static async Task<string> Guard(Func<Task<string>> body)
    {
        try { return await body(); }
        catch (ToolException ex) { return ErrJson(ex.Code.ToString(), ex.Message, ex.SuggestedRecovery); }
        // (the two existing ⚠⚠ comment blocks stay here, unchanged)
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogException(ex);
            return ErrJson("INTERNAL", ex.Message, "re-check arguments and retry");
        }
    }

    public static async Task<CallToolResult> GuardImage(Func<Task<CallToolResult>> body)
    {
        try { return await body(); }
        catch (ToolException ex) { return ErrResult(ex.Code.ToString(), ex.Message, ex.SuggestedRecovery); }
        // ⚠ Same critical filter as Guard above, and for the same reason - see the comment there.
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogException(ex);
            return ErrResult("INTERNAL", ex.Message, "re-check arguments and retry");
        }
    }

    // The error DTO. `log` is ADDITIVE (spec §5.5) and absent - not null - when there is no pointer:
    // Json does not ignore nulls, so the two shapes are explicit. Without a pointer the bytes are exactly
    // what they were before logging existed (ToolResponseCriticalTests pins them).
    private static string ErrJson(string code, string message, string? recovery)
    {
        var pointer = ToolLog.Pointer();
        return pointer is null
            ? JsonSerializer.Serialize(new { error = code, message, suggestedRecovery = recovery }, Json)
            : JsonSerializer.Serialize(new { error = code, message, suggestedRecovery = recovery, log = pointer }, Json);
    }

    private static CallToolResult ErrResult(string code, string message, string? recovery) => new()
    { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = ErrJson(code, message, recovery) } } };

    // spec §4.5: the type and stack an INTERNAL error used to lose. A .NET stack carries frames, never
    // argument values (spec §6.4); msg is UI text and is masked per §6.3/§6.4 by the provider.
    private static void LogException(Exception ex) =>
        LogEvent.Emit(ToolLog.Logger, LogLevel.Error, "tool.exception",
            LogEvent.F("exType", ex.GetType().FullName), LogEvent.F("hresult", ex.HResult),
            LogEvent.F("stack", ex.StackTrace), LogEvent.Ui("msg", ex.Message));
```

Shape check: the old ToolException branch serialized `suggestedRecovery = ex.SuggestedRecovery` (`string?`) and the INTERNAL branch `(string?)"re-check arguments and retry"`; `ErrJson` takes `string? recovery`, so both keep the same three keys in the same order. If anything in your change would alter those bytes when the pointer is null, STOP and report `[original] -> [yours] because <reason>`.

- [ ] **Step 5:** Build; run `--filter "FullyQualifiedName~ToolResponseLoggingTests|FullyQualifiedName~ToolResponseCriticalTests|FullyQualifiedName~ReadOnlyGateTests"` → all pass.
- [ ] **Step 6:** Commit `feat(server): log INTERNAL exceptions and point errors at the log`.

---

### Task 10: Flags, host facts, and Program wiring

**Files:** Modify `src/FlaUI.Mcp.Server/ServerOptions.cs`, `src/FlaUI.Mcp.Server/Program.cs` (LF); Create `src/FlaUI.Mcp.Server/Logging/HostFacts.cs`, `test/FlaUI.Mcp.Tests/Logging/LogOptionsTests.cs`.

- [ ] **Step 1: Failing tests** `LogOptionsTests.cs`:

```csharp
// (license header)

using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class LogOptionsTests
{
    [Fact] public void Logging_is_on_at_info_by_default()
    {
        var o = ServerOptions.FromArgs(new string[0]);
        Assert.False(o.NoLog);
        Assert.Equal(LogLevel.Information, o.LogLevel);
    }
    [Fact] public void No_log_flag_disables_it() => Assert.True(ServerOptions.FromArgs(new[] { "--no-log" }).NoLog);
    [Theory]
    [InlineData("--log-level=debug", LogLevel.Debug)]
    [InlineData("--log-level=info", LogLevel.Information)]
    [InlineData("--log-level=warning", LogLevel.Warning)]
    [InlineData("--log-level=garbage", LogLevel.Information)]
    [InlineData("--log-level=", LogLevel.Information)]
    public void Log_level_parses_with_info_fallback(string arg, LogLevel expected) =>
        Assert.Equal(expected, ServerOptions.FromArgs(new[] { arg }).LogLevel);

    [Fact] public void Flag_names_never_carry_values()
    {
        Assert.Equal(new[] { "--redaction-rules", "--log-level", "--overlay" },
            HostFacts.FlagNames(new[] { "--redaction-rules", @"C:\Users\me\rules.json", "--log-level=debug", "--overlay" }));
    }

    [Fact] public void Host_facts_never_throw_and_use_known_words()
    {
        Assert.Contains(HostFacts.Session(), new[] { "console", "rdp", "unknown" });
        Assert.Contains(HostFacts.DpiAwareness(), new[] { "unaware", "system", "permonitor", "permonitorv2", "unknown" });
        Assert.True(HostFacts.DpiScale() > 0);
    }
}
```

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: Modify `ServerOptions.cs`.** Add `using Microsoft.Extensions.Logging;`. Append two parameters to the record header (after `bool CaptureAuditSignal = false`): `bool NoLog = false, LogLevel LogLevel = LogLevel.Information`. In `FromArgs`, after `CaptureAuditSignal: args.Contains("--capture-audit-signal")` add `, NoLog: args.Contains("--no-log"), LogLevel: ParseLogLevel(args)`. Add the method:

```csharp
    // "--log-level=debug|info|warning". Absent or garbage -> Information (spec §5.4).
    private static LogLevel ParseLogLevel(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--log-level=", System.StringComparison.Ordinal));
        return arg?["--log-level=".Length..] switch
        {
            "debug" => LogLevel.Debug,
            "warning" => LogLevel.Warning,
            _ => LogLevel.Information,
        };
    }
```

- [ ] **Step 4: Implement** `HostFacts.cs`:

```csharp
// (license header)

using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>`server.start` facts (spec §5.2). Every call is fail-soft to "unknown". These are the two facts
/// that each cost a session to diagnose before (DPI mismatch at 125%, ROADMAP item 16 foreground lock).</summary>
public static class HostFacts
{
    public static string Session()
    {
        try { return GetSystemMetrics(SM_REMOTESESSION) != 0 ? "rdp" : "console"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return "unknown"; }
    }

    public static string DpiAwareness()
    {
        try
        {
            var ctx = GetThreadDpiAwarenessContext();
            if (AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return "permonitorv2";
            return GetAwarenessFromDpiAwarenessContext(ctx) switch { 0 => "unaware", 1 => "system", 2 => "permonitor", _ => "unknown" };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return "unknown"; }
    }

    public static double DpiScale()
    {
        try { var dpi = GetDpiForSystem(); return dpi == 0 ? 1.0 : dpi / 96.0; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return 1.0; }
    }

    /// <summary>The `--x` flags present, without values: `--redaction-rules <path>` and `--log-level=debug`
    /// yield their names only, so no path or free text reaches the log.</summary>
    public static string[] FlagNames(string[] args) =>
        args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.Split('=')[0]).ToArray();

    private const int SM_REMOTESESSION = 0x1000;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
```

- [ ] **Step 5:** Build; run `--filter "FullyQualifiedName~LogOptionsTests|FullyQualifiedName~ServerOptionsTests"` → all pass.
- [ ] **Step 6: Wire `Program.cs`.** Add usings `using FlaUI.Mcp.Server.Logging;` and `using Microsoft.Extensions.Logging.Console;`.

  (a) After `AppDomain.CurrentDomain.ProcessExit += ...` (line 90-91), insert:

```csharp
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
```

  (b) Replace lines 98-99 (`builder.Logging.ClearProviders();` and `builder.Logging.AddConsole(...)`) with:

```csharp
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
// The file provider needs Debug events, so the global floor is Trace; the console keeps its old Information
// floor, and our own structured events reach stderr only at Warning+ (plan "Spec adjustments" 4).
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Information);
builder.Logging.AddFilter<ConsoleLoggerProvider>("FlaUI.Mcp", LogLevel.Warning);
if (fileLog is not null) builder.Logging.AddProvider(fileLog);
```

  (c) After `builder.Services.AddSingleton<TrayTools>();` (line 190) insert:

```csharp
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
```

  (d) In the `InputGuard` factory (lines 151-163) add a final argument `logger: sp.GetService<ILogger<InputGuard>>()` (the parameter is added in Task 13; until then, skip this sub-step and do it in Task 13 Step 5). In the `WindowCaptureCoordinator.ForPerception(...)` registration (lines 134-138) add `logger: sp.GetService<ILoggerFactory>()?.CreateLogger<FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator>()` likewise in Task 12.

  (e) Change the MCP chain (lines 227-237) to add the filter after `.WithToolsFromAssembly()`:

```csharp
    .WithToolsFromAssembly()
    .WithRequestFilters(f => f.AddCallToolFilter(next => async (ctx, ct) =>
        ctx.Services?.GetService<ToolCallLogFilter>() is { } log
            ? await log.RunAsync(ctx.Params?.Name ?? "", ctx.Params?.Arguments, () => next(ctx, ct))
            : await next(ctx, ct)));
```

  (f) Replace `await builder.Build().RunAsync();` (line 239) with:

```csharp
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
```

  `IHostApplicationLifetime` is in `Microsoft.Extensions.Hosting`, already imported.

- [ ] **Step 7:** Build → `Build succeeded.` Headless gate → green. Manual smoke: run `src/FlaUI.Mcp.Server/bin/Debug/net10.0-windows10.0.19041.0/win-x64/flaui-mcp.exe` with `FLAUI_MCP_LOG_DIR` set to a temp dir, close stdin (Ctrl+Z, Enter), and confirm the dir holds one file whose lines are `server.start` then `server.stop`.
- [ ] **Step 8:** Commit `feat(server): wire the structured log (flags, server.start/stop, filter)`.

---

### Task 11: WindowManager events (`window.bind`, `focus.attempt`, `action.blocked`)

**Files:** Modify `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`; Create `test/FlaUI.Mcp.Tests/Logging/WindowManagerLoggingTests.cs`.

- [ ] **Step 1: Failing tests:**

```csharp
// (license header)

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class WindowManagerLoggingTests
{
    [Fact]
    public void A_fresh_lazy_mint_emits_window_bind_and_a_reuse_does_not()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        var pid = Environment.ProcessId;
        var id = wm.LazyHandleFor((IntPtr)0x7FFF0001, pid);
        Assert.Equal(id, wm.LazyHandleFor((IntPtr)0x7FFF0001, pid));
        var ev = log.Inner.Single("window.bind");
        Assert.Equal(id, ev.Fields.Single(f => f.Key == "handle").Value);
        Assert.Equal((long)0x7FFF0001, ev.Fields.Single(f => f.Key == "hwnd").Value);
        Assert.Equal(pid, ev.Fields.Single(f => f.Key == "pid").Value);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, ev.Fields.Single(f => f.Key == "process").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "title").UiText);
    }

    [Fact]
    public async Task ActionBlockedPending_emits_action_blocked_and_rethrows()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        var blocked = Task.FromException<int>(new ToolException(ToolErrorCode.ActionBlockedPending, "blocked"));
        var ex = await Assert.ThrowsAsync<ToolException>(() => wm.ObserveBlockedAsync(blocked));
        Assert.Equal(ToolErrorCode.ActionBlockedPending, ex.Code);
        var ev = log.Inner.Single("action.blocked");
        Assert.Contains(ev.Fields, f => f.Key == "fgHwnd");
        Assert.True(ev.Fields.Single(f => f.Key == "fgTitle").UiText);
    }

    [Fact]
    public async Task Other_failures_are_not_action_blocked()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        await Assert.ThrowsAsync<ToolException>(() => wm.ObserveBlockedAsync(
            Task.FromException<int>(new ToolException(ToolErrorCode.WindowHandleStale, "stale"))));
        Assert.Empty(log.Inner.Events);
    }

    [Fact]
    public void Focus_not_gained_names_the_foreground_owner()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        wm.EmitFocus(new WindowHandle("w9"), new FocusResult(false, (IntPtr)0x10, (IntPtr)0x20));
        var ev = log.Inner.Single("focus.attempt");
        Assert.Equal("w9", ev.Fields.Single(f => f.Key == "target").Value);
        Assert.False((bool)ev.Fields.Single(f => f.Key == "gained").Value!);
        Assert.Equal((long)0x20, ev.Fields.Single(f => f.Key == "fgHwnd").Value);
        Assert.Contains(ev.Fields, f => f.Key == "fgProcess");
    }

    [Fact]
    public void Focus_gained_carries_no_foreground_fields()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        new WindowManager(d, log).EmitFocus(new WindowHandle("w9"), new FocusResult(true, (IntPtr)0x10, (IntPtr)0x10));
        Assert.DoesNotContain(log.Inner.Single("focus.attempt").Fields, f => f.Key.StartsWith("fg", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: Constructor.** Add `using FlaUI.Mcp.Core.Logging;` and `using Microsoft.Extensions.Logging;`. Add the field `private readonly ILogger? _log;` next to `private int _counter;` (line 60). Change the constructor (lines 69-74) to:

```csharp
    public WindowManager(AutomationDispatcher dispatcher, ILogger<WindowManager>? logger = null)
    {
        _log = logger;   // set FIRST: nothing below may log before it exists
        _dispatcher = dispatcher;
        // UIA objects must be created on the STA thread that uses them.
        _automation = _dispatcher.RunQueryAsync(() => new UIA3Automation()).GetAwaiter().GetResult();
    }
```

- [ ] **Step 4: `window.bind`.** Add the helpers (anywhere in the class, e.g. right after `LazyHandleFor`):

```csharp
    // spec §5.2 window.bind: gives `wN` a post-mortem meaning. Win32 only (WindowTitle does not block on a
    // hung window); the title is UI text, masked by the provider by THIS window's own process.
    private void EmitBind(string id, IntPtr hwnd, int pid) =>
        LogEvent.Emit(_log, LogLevel.Information, "window.bind",
            LogEvent.F("handle", id), LogEvent.F("hwnd", hwnd.ToInt64()), LogEvent.F("pid", pid),
            LogEvent.F("process", SafeProcessName(pid)),
            LogEvent.Ui("title", hwnd == IntPtr.Zero ? null : WindowTitle(hwnd)));
```

  In `Register` (lines 280-294) hoist `hwnd` so it can be logged, replacing the method with:

```csharp
    internal WindowHandle Register(Window window, int pid)
    {
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _ids[id] = pid;                 // unconditional gate/identity write (mirrors the lazy mint site)
        _handles[id] = window;
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            _hwnds[id] = hwnd;
            if (hwnd != IntPtr.Zero) _byHwnd[hwnd] = id;
        }
        catch { /* no hwnd */ }
        TryWatchProcessExit(id, pid);
        EmitBind(id, hwnd, pid);
        return new WindowHandle(id);
    }
```

  (The existing bare `catch { /* no hwnd */ }` is pre-existing and stays as is.) In `LazyHandleFor`, insert `EmitBind(id, hwnd, pid);` between `TryWatchProcessExit(id, pid);` and `return id;` (lines 316-317). The reuse `return existing;` path emits nothing.

- [ ] **Step 5: `focus.attempt`.** Add:

```csharp
    // spec §5.2 focus.attempt. Foreground fields only when focus was NOT gained (that is the diagnostic case).
    internal void EmitFocus(WindowHandle handle, FocusResult r)
    {
        if (r.ForegroundGained)
        {
            LogEvent.Emit(_log, LogLevel.Information, "focus.attempt", LogEvent.F("target", handle.Id), LogEvent.F("gained", true));
            return;
        }
        GetWindowThreadProcessId(r.ForegroundHwnd, out uint fgPid);
        LogEvent.Emit(_log, LogLevel.Information, "focus.attempt",
            LogEvent.F("target", handle.Id), LogEvent.F("gained", false),
            LogEvent.F("fgHwnd", r.ForegroundHwnd.ToInt64()),
            LogEvent.F("fgHandle", _byHwnd.TryGetValue(r.ForegroundHwnd, out var fgId) ? fgId : null),
            LogEvent.F("fgProcess", fgPid == 0 ? null : SafeProcessName((int)fgPid)));
    }
```

  In `FocusWithWhyNotAsync` (lines 647-655) replace `return new FocusResult(hwnd != IntPtr.Zero && fg == hwnd, hwnd, fg);` with:

```csharp
            var result = new FocusResult(hwnd != IntPtr.Zero && fg == hwnd, hwnd, fg);
            EmitFocus(handle, result);
            return result;
```

  If `GetWindowThreadProcessId` is declared in this file with a different signature than `(IntPtr, out uint)`, use the existing declaration (the call in `TryGetAppName`, line 160, shows its shape) and STOP-report if it differs.

- [ ] **Step 6: `action.blocked`.** Add:

```csharp
    // spec §5.2 action.blocked: the window that holds the foreground when an action hangs is usually the
    // modal that blocked it. Win32 only; observed here because the dispatcher's AwaitWithTimeout is static
    // and does not know the window.
    internal async Task<T> ObserveBlockedAsync<T>(Task<T> work)
    {
        try { return await work.ConfigureAwait(false); }
        catch (ToolException ex) when (ex.Code == ToolErrorCode.ActionBlockedPending)
        {
            var fg = GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
            LogEvent.Emit(_log, LogLevel.Warning, "action.blocked",
                LogEvent.F("fgHwnd", fg.ToInt64()),
                LogEvent.F("fgHandle", fg != IntPtr.Zero && _byHwnd.TryGetValue(fg, out var id) ? id : null),
                LogEvent.F("fgProcess", pid == 0 ? null : SafeProcessName((int)pid)),
                LogEvent.Ui("fgTitle", fg == IntPtr.Zero ? null : WindowTitle(fg)));
            throw;
        }
    }
```

  In `RunOnWindowActionAsync` (lines 600-632): change `return _dispatcher.RunActionAsync(() =>` to `var work = _dispatcher.RunActionAsync(() =>` and, after the statement's closing `}, timeoutMs);`, add the line `return _log is null ? work : ObserveBlockedAsync(work);`. The synchronous `WindowHandleStale` throws above it are unchanged.

- [ ] **Step 7:** Build; run `--filter "FullyQualifiedName~WindowManagerLoggingTests|FullyQualifiedName~WindowLivenessTests"` → all pass. Headless gate → green.
- [ ] **Step 8: Mutation check.** Delete the `EmitBind(id, hwnd, pid);` line in `LazyHandleFor` → the first test FAILS. Restore.
- [ ] **Step 9:** Commit `feat(core): window.bind, focus.attempt and action.blocked events`.

---

### Task 12: Perception events (`selector.resolve`, `ref.resolve`, `wait.end`, `capture.decision`)

**Files:** Modify `PerceptionManager.cs`, `RefRegistry.cs`, `WaitCoordinator.cs`, `WindowCaptureCoordinator.cs` (all in `src/FlaUI.Mcp.Core/Perception/`), `src/FlaUI.Mcp.Server/Program.cs`; Create `test/FlaUI.Mcp.Tests/Logging/PerceptionLoggingTests.cs`.

- [ ] **Step 1: Failing tests:**

```csharp
// (license header)

using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class PerceptionLoggingTests
{
    private static ElementDescriptor Desc(string aid, string name) =>
        new(new[] { 1, 2 }, ControlType.Button, aid, name, null, Array.Empty<int>());

    [Fact]
    public void Lookup_of_a_registered_ref_emits_ref_resolve_found()
    {
        var log = new CapturingLogger<RefRegistry>();
        var refs = new RefRegistry(log);
        var r = refs.Register("w1", Desc("btnOk", "OK"), cached: null);
        refs.Lookup("w1", r);
        var ev = log.Inner.Single("ref.resolve");
        Assert.Equal(r, ev.Fields.Single(f => f.Key == "ref").Value);
        Assert.True((bool)ev.Fields.Single(f => f.Key == "found").Value!);
        Assert.Equal("Button", ev.Fields.Single(f => f.Key == "controlType").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "name").UiText);
        Assert.True(ev.Fields.Single(f => f.Key == "automationId").UiText);
    }

    [Fact]
    public void Lookup_of_an_unknown_ref_emits_found_false_then_throws()
    {
        var log = new CapturingLogger<RefRegistry>();
        var ex = Assert.Throws<ToolException>(() => new RefRegistry(log).Lookup("w1", "e99"));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
        Assert.False((bool)log.Inner.Single("ref.resolve").Fields.Single(f => f.Key == "found").Value!);
    }

    [Fact]
    public void A_registry_without_a_logger_emits_nothing_and_still_works()
    {
        var refs = new RefRegistry();
        refs.Lookup("w1", refs.Register("w1", Desc("a", "b"), cached: null));
    }

    [Fact]
    public async Task A_failing_wait_emits_wait_end_with_its_code()
    {
        var log = new CapturingLogger<WaitCoordinator>();
        using var d = new AutomationDispatcher();
        var pm = new PerceptionManager(new WindowManager(d), new RefRegistry(), new SnapshotCache());
        var wc = new WaitCoordinator(pm, log);
        // An unknown handle either throws (code set) or returns unsatisfied (code null); both must log.
        string? thrown = null;
        try { await wc.WaitForAsync(new WindowHandle("w999"), "name", "Save", "exists", null, timeoutMs: 100, pollIntervalMs: 50); }
        catch (ToolException ex) { thrown = ex.Code.ToString(); }
        var ev = log.Inner.Single("wait.end");
        Assert.Equal("wait_for", ev.Fields.Single(f => f.Key == "kind").Value);
        Assert.False((bool)ev.Fields.Single(f => f.Key == "satisfied").Value!);
        Assert.Equal(thrown, ev.Fields.Single(f => f.Key == "code").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "value").UiText);
    }

    [Fact]
    public async Task A_refused_capture_emits_capture_decision_with_its_code()
    {
        var log = new CapturingLogger<WindowCaptureCoordinator>();
        var wcc = new WindowCaptureCoordinator(
            (_, _) => throw new ToolException(ToolErrorCode.TargetDenied, "denied"),
            new FakeWindowImageSource(_ => throw new InvalidOperationException("not reached")),
            CaptureRetryOptions.Default, logger: log);
        await Assert.ThrowsAsync<ToolException>(() => wcc.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 1600));
        var ev = log.Inner.Single("capture.decision");
        Assert.False((bool)ev.Fields.Single(f => f.Key == "ok").Value!);
        Assert.Equal("TargetDenied", ev.Fields.Single(f => f.Key == "code").Value);
        Assert.Equal("Window", ev.Fields.Single(f => f.Key == "scope").Value);
    }

    [Fact]
    public void Selector_resolve_event_shape()
    {
        var log = new CapturingLogger<PerceptionManager>();
        using var d = new AutomationDispatcher();
        var pm = new PerceptionManager(new WindowManager(d), new RefRegistry(), new SnapshotCache(), logger: log);
        pm.EmitSelectorResolve(new Selector(AutomationId: "btnOk", Name: "OK", ControlType: "Button"), candidates: 2, chosen: null);
        var ev = log.Inner.Single("selector.resolve");
        Assert.Equal("selector", ev.Fields.Single(f => f.Key == "path").Value);
        Assert.Equal(2, ev.Fields.Single(f => f.Key == "candidates").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "name").UiText);
        Assert.False(ev.Fields.Single(f => f.Key == "controlType").UiText);
    }
}
```

  `FakeWindowImageSource` is the existing test fake (`test/FlaUI.Mcp.Tests/Perception/FakeWindowImageSource.cs:15`, ctor `(Func<Size, Bitmap?> make)`, namespace `FlaUI.Mcp.Tests.Perception`): add `using FlaUI.Mcp.Tests.Perception;`.

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: `RefRegistry` (`ref.resolve`).** Add usings `FlaUI.Mcp.Core.Logging`, `Microsoft.Extensions.Logging`. Add a field and an explicit constructor (the class has none today, so `new RefRegistry()` keeps compiling):

```csharp
    private readonly ILogger? _log;
    public RefRegistry(ILogger<RefRegistry>? logger = null) => _log = logger;
```

  Replace `Lookup` (lines 88-100) with:

```csharp
    internal Entry Lookup(string windowId, string @ref)
    {
        Entry? found = null;
        lock (_gate)
        {
            if (_byWindow.TryGetValue(windowId, out var map) && map.TryGetValue(@ref, out var e)) found = e;
            else if (_eventByWindow.TryGetValue(windowId, out var eventMap) && eventMap.TryGetValue(@ref, out var ee)) found = ee;
        }
        // spec §5.2 ref.resolve (outside the lock). `found` = the ref is registered for this window.
        LogEvent.Emit(_log, LogLevel.Information, "ref.resolve",
            LogEvent.F("ref", @ref), LogEvent.F("found", found is not null),
            LogEvent.F("controlType", found?.Descriptor.ControlType.ToString()),
            LogEvent.Ui("automationId", found?.Descriptor.AutomationId),
            LogEvent.Ui("name", found?.Descriptor.Name));
        return found ?? throw new ToolException(ToolErrorCode.RefNotFound,
            $"Ref '{@ref}' is not in the current snapshot of window '{windowId}'.",
            "re-acquire the element with desktop_find (targeted, and it registers additively without superseding your other refs), or take a fresh desktop_snapshot");
    }
```

  (Same two lookups, same exception text and recovery; only the throw moved out of the lock.) If `_eventByWindow`'s type makes `ee` a different type than `Entry`, STOP and report.

- [ ] **Step 4: `PerceptionManager` (`selector.resolve`).** Add usings; field `private readonly ILogger? _log;`; constructor becomes `public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache, SensitivityClassifier? classifier = null, ILogger<PerceptionManager>? logger = null)` with `_log = logger;` as its first statement. Add:

```csharp
    internal void EmitSelectorResolve(Selector sel, int candidates, string? chosen) =>
        LogEvent.Emit(_log, LogLevel.Debug, "selector.resolve",
            LogEvent.F("path", "selector"), LogEvent.Ui("automationId", sel.AutomationId), LogEvent.Ui("name", sel.Name),
            LogEvent.F("controlType", sel.ControlType), LogEvent.F("candidates", candidates), LogEvent.F("chosen", chosen));
```

  In `ResolveSelectorOnSta`, insert `EmitSelectorResolve(sel, hits.Count, null);` immediately before `if (hits.Count == 0)` (line 240) guarded by `if (hits.Count != 1)`, and `EmitSelectorResolve(sel, 1, mintedRef);` immediately before `return (h.El, mintedRef);` (line 253). In `FindAsync`, immediately before `return new FindResult(matches, total, total > max);` (line 703), insert:

```csharp
            LogEvent.Emit(_log, LogLevel.Debug, "selector.resolve",
                LogEvent.F("path", "find"), LogEvent.Ui("automationId", query.AutomationId), LogEvent.Ui("name", query.Name),
                LogEvent.F("controlType", query.ControlType), LogEvent.F("candidates", total), LogEvent.F("returned", matches.Count));
```

  (`FindQuery` is `record FindQuery(string? AutomationId, string? Name, string NameMatch, string? ControlType, bool EnabledOnly, bool IgnoreCase = false)`, `src/FlaUI.Mcp.Core/Perception/FindQuery.cs:19-25`. `FindAsync`'s parameter is named `query`, line 557.)

- [ ] **Step 5: `WaitCoordinator` (`wait.end`).** Add usings; replace the constructor (line 33) with:

```csharp
    private readonly ILogger? _log;
    public WaitCoordinator(PerceptionManager perception, ILogger<WaitCoordinator>? logger = null)
    { _perception = perception; _log = logger; }
```

  Rename the existing `public async Task<WaitForResult> WaitForAsync(` (line 243) to `private async Task<WaitForResult> WaitForCoreAsync(` and `public async Task<WaitStableResult> WaitForStableAsync(` (line 133) to `private async Task<WaitStableResult> WaitForStableCoreAsync(`, keeping their parameter lists. Add public wrappers with the SAME signatures and defaults as the originals:

```csharp
    public async Task<WaitForResult> WaitForAsync(WindowHandle handle, string by, string value,
        string until, string? equals, int timeoutMs, int pollIntervalMs, bool includeOffscreen = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await WaitForCoreAsync(handle, by, value, until, equals, timeoutMs, pollIntervalMs, includeOffscreen);
            EmitWaitEnd("wait_for", until, by, value, r.ElapsedMs, r.Satisfied, null);
            return r;
        }
        catch (ToolException ex) { EmitWaitEnd("wait_for", until, by, value, (int)sw.ElapsedMilliseconds, false, ex.Code.ToString()); throw; }
    }

    public async Task<WaitStableResult> WaitForStableAsync(WindowHandle handle, string? by, string? value,
        bool includeText, int quietMs, int timeoutMs, int pollIntervalMs, string? scopeRef = null, bool includeOffscreen = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await WaitForStableCoreAsync(handle, by, value, includeText, quietMs, timeoutMs, pollIntervalMs, scopeRef, includeOffscreen);
            EmitWaitEnd("wait_for_stable", "stable", by, value, r.ElapsedMs, r.Stable, null);
            return r;
        }
        catch (ToolException ex) { EmitWaitEnd("wait_for_stable", "stable", by, value, (int)sw.ElapsedMilliseconds, false, ex.Code.ToString()); throw; }
    }

    private void EmitWaitEnd(string kind, string condition, string? by, string? value, int ms, bool satisfied, string? code) =>
        LogEvent.Emit(_log, LogLevel.Debug, "wait.end",
            LogEvent.F("kind", kind), LogEvent.F("condition", condition), LogEvent.F("by", by), LogEvent.Ui("value", value),
            LogEvent.F("ms", ms), LogEvent.F("satisfied", satisfied), LogEvent.F("code", code));
```

  Before writing the wrappers, open lines 133-135 and 243-245 and copy the parameter lists exactly; if they differ from the ones above, the ORIGINALS win and you report the difference.

- [ ] **Step 6: `WindowCaptureCoordinator` (`capture.decision`).** Add usings; field `private readonly ILogger? _log;`; append `ILogger? logger = null` as the LAST constructor parameter (after `desktopMasks`) with `_log = logger;`; append `ILogger? logger = null` to `ForPerception` and pass `logger: logger` in its `new(...)`. Rename `public async Task<WindowCaptureOutcome> CaptureAsync(` (line 130) to `private async Task<WindowCaptureOutcome> CaptureCoreAsync(` and add:

```csharp
    public async Task<WindowCaptureOutcome> CaptureAsync(WindowHandle handle, string? @ref, CaptureScope scope, int maxWidth)
    {
        try
        {
            var o = await CaptureCoreAsync(handle, @ref, scope, maxWidth);
            LogEvent.Emit(_log, LogLevel.Information, "capture.decision",
                LogEvent.F("scope", scope.ToString()), LogEvent.F("ok", true), LogEvent.F("method", o.Result.CaptureMethod),
                LogEvent.F("warnings", o.Result.CaptureWarnings.Select(w => w.Code).ToArray()), LogEvent.F("code", null));
            return o;
        }
        catch (ToolException ex)
        {
            LogEvent.Emit(_log, LogLevel.Information, "capture.decision",
                LogEvent.F("scope", scope.ToString()), LogEvent.F("ok", false), LogEvent.F("method", null),
                LogEvent.F("warnings", Array.Empty<string>()), LogEvent.F("code", ex.Code.ToString()));
            throw;
        }
    }
```

  `CaptureGeometryCallSiteTests` counts `ResolveWindowCaptureGeometryAsync` call sites (THREE across `src/`); this change adds none. In `Program.cs` change the `ForPerception(...)` registration (lines 134-138) to pass `logger: sp.GetService<ILoggerFactory>()?.CreateLogger<FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator>()` as a named final argument.

- [ ] **Step 7:** Build; run `--filter "FullyQualifiedName~PerceptionLoggingTests"` → 6 passed. Headless gate → green (this touches hot paths: every existing Perception/Wait/Capture test must stay green).
- [ ] **Step 8: Mutation check.** Remove the `LogEvent.Emit` in `Lookup` → both ref tests FAIL. Restore.
- [ ] **Step 9:** Commit `feat(core): selector, ref, wait and capture decision events`.

---

### Task 13: `input.decision`

**Files:** Modify `src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs`, `InputGuard.cs`, `src/FlaUI.Mcp.Server/Program.cs`; Create `test/FlaUI.Mcp.Tests/Logging/InputDecisionLoggingTests.cs`.

- [ ] **Step 1: Failing tests.** `FakePlatformEnvironment` (`test/.../Interaction/FakePlatformEnvironment.cs:12`) and `RecordingSyntheticInput` (`test/.../Interaction/RecordingSyntheticInput.cs:16`) are public test fakes in `FlaUI.Mcp.Tests.Interaction`. `StubLeaseProvider` is PRIVATE to `InputGuardTests` (line 284), so this file declares its own `LeaseStub`. `KeyType` authorizes with action `"type"` (`InputGuard.cs:119`).

```csharp
// (license header)

using System;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Tests.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class InputDecisionLoggingTests
{
    private const string Sid = "S-1-5-21-1";
    private static DateTime Now => new(2030, 1, 1, 0, 0, 30, DateTimeKind.Utc);

    private static (InputGuard, CapturingLogger<InputGuard>) Build(InputLease? lease)
    {
        var env = new FakePlatformEnvironment { CanDeliver = true, ForegroundRoot = nint.Zero };
        var log = new CapturingLogger<InputGuard>();
        var guard = new InputGuard(new RecordingSyntheticInput(env), env, new LeaseStub(lease),
            new ActionBudget(60, 60), new InputAudit(TextWriter.Null),
            currentSid: Sid, isElevated: false, allowElevation: false, clock: () => Now, logger: log);
        return (guard, log);
    }

    [Fact]
    public void A_refusal_logs_allowed_false_with_its_code()
    {
        var (g, log) = Build(null);
        Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        var ev = log.Inner.Single("input.decision");
        Assert.False((bool)ev.Fields.Single(f => f.Key == "allowed").Value!);
        Assert.Equal("InputNotLeased", ev.Fields.Single(f => f.Key == "code").Value);
        Assert.Equal("type", ev.Fields.Single(f => f.Key == "action").Value);
    }

    [Fact]
    public void An_allowed_input_logs_the_remaining_budget()
    {
        var (g, log) = Build(new InputLease(new DateTime(2030, 1, 1, 0, 1, 0, DateTimeKind.Utc), Sid, Array.Empty<string>()));
        g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad"));
        var ev = log.Inner.Single("input.decision");
        Assert.True((bool)ev.Fields.Single(f => f.Key == "allowed").Value!);
        Assert.Equal(59, ev.Fields.Single(f => f.Key == "budgetLeft").Value);
    }

    [Fact]
    public void Remaining_counts_down_and_prunes_expired_hits()
    {
        var b = new ActionBudget(3, 60);
        Assert.Equal(3, b.Remaining(1, Now));
        b.TryConsume(1, Now, default);
        Assert.Equal(2, b.Remaining(1, Now));
        Assert.Equal(3, b.Remaining(1, Now.AddSeconds(61)));
    }

    private sealed class LeaseStub(InputLease? lease) : ILeaseProvider
    {
        public InputLease? Read(out DateTime lastWriteUtc) { lastWriteUtc = default; return lease; }
    }
}
```

- [ ] **Step 2:** Build → FAIL.
- [ ] **Step 3: `ActionBudget.Remaining`.** Add after `SecondsUntilFreeSlot`:

```csharp
    /// <summary>Slots left for this window right now (prunes expired hits; never consumes). For the
    /// structured log's input.decision.</summary>
    public int Remaining(nint window, DateTime now)
    {
        lock (_gate)
        {
            if (!_hits.TryGetValue(window, out var q)) return _max;
            var cutoff = now.AddSeconds(-_windowSeconds);
            while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
            return Math.Max(0, _max - q.Count);
        }
    }
```

- [ ] **Step 4: `InputGuard`.** Add usings; field `private readonly ILogger? _log;`; append `ILogger<InputGuard>? logger = null` after `Func<DateTime>? clock = null` in the constructor, with `_log = logger;`. Rename `private void Authorize(` (line 39) to `private void AuthorizeCore(`, and add:

```csharp
    private void Authorize(ActionTarget primary, string action, int payloadLength, ActionTarget? secondary = null)
    {
        try { AuthorizeCore(primary, action, payloadLength, secondary); }
        catch (ToolException ex) { EmitDecision(primary, action, false, ex.Code.ToString()); throw; }
        EmitDecision(primary, action, true, null);
    }

    // spec §5.2 input.decision (plan "Spec adjustments" 5: lease state is visible as code InputNotLeased).
    private void EmitDecision(ActionTarget primary, string action, bool allowed, string? code) =>
        LogEvent.Emit(_log, LogLevel.Information, "input.decision",
            LogEvent.F("action", action), LogEvent.F("allowed", allowed), LogEvent.F("code", code),
            LogEvent.F("budgetLeft", _budget.Remaining(primary.Root, _clock())));
```

- [ ] **Step 5: `Program.cs`.** In the `InputGuard` factory (lines 151-163) add the named argument `logger: sp.GetService<ILogger<InputGuard>>()` after `allowElevation: opts.AllowElevation`.
- [ ] **Step 6:** Build; run `--filter "FullyQualifiedName~InputDecisionLoggingTests|FullyQualifiedName~InputGuardTests|FullyQualifiedName~ActionBudgetTests"` → all pass. Headless gate → green.
- [ ] **Step 7:** Commit `feat(core): input.decision event and ActionBudget.Remaining`.

---

### Task 14: End-to-end tests (no content on disk; real server process)

**Files:** Create `test/FlaUI.Mcp.Tests/Logging/NoContentOnDiskTests.cs` (headless), `test/FlaUI.Mcp.Tests/Logging/ServerLogProcessTests.cs` (Desktop).

- [ ] **Step 1: No content on disk** (spec §8.5). Drives the real filter + real provider + real catalog:

```csharp
// (license header)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public sealed class NoContentOnDiskTests : IDisposable
{
    private const string Sentinel = "ZQX-sentinel-9f3a";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-nocontent-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class NotSensitive : ICallSensitivity
    {
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) => (false, false);
    }

    [Theory]
    [InlineData("desktop_type", """{"window":"w1","text":"ZQX-sentinel-9f3a"}""")]
    [InlineData("desktop_paste_text", """{"window":"w1","text":"ZQX-sentinel-9f3a"}""")]
    [InlineData("desktop_set_value", """{"window":"w1","value":"ZQX-sentinel-9f3a","ref":"e1"}""")]
    [InlineData("desktop_clipboard_set", """{"text":"ZQX-sentinel-9f3a"}""")]
    public async Task The_sentinel_bytes_never_reach_the_log_file(string tool, string json)
    {
        var hasher = new ContentHasher();
        var provider = JsonlFileLoggerProvider.TryCreate(new JsonlLogOptions(_dir, 1, DateTime.UtcNow, LogLevel.Trace),
            new LogMasker(hasher, _ => false), out _)!;
        using (var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)))
        {
            var filter = new ToolCallLogFilter(factory.CreateLogger("t"),
                ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly), new NotSensitive(), hasher);
            var args = JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            // The tool "fails" with a message that quotes the content, the worst case (spec §6.4).
            await filter.RunAsync(tool, args, () => new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text =
                    $$"""{"error":"INTERNAL","message":"rejected {{Sentinel}}","suggestedRecovery":null}""" } },
            }));
        }
        var bytes = Directory.GetFiles(_dir).SelectMany(File.ReadAllBytes).ToArray();
        Assert.NotEmpty(bytes);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Sentinel)));
    }
}
```

- [ ] **Step 2:** Run it → 4 passed. **Mutation checks:** (a) in `ArgumentClassifier.Walk`'s `default:` branch return `JsonValue.Create(s)` instead of the mask → all 4 FAIL; (b) in `LogMasker.Apply` set `maskMsgForContent = false` → all 4 FAIL (the INTERNAL message quotes the sentinel). Restore both.
- [ ] **Step 3: Server process test** (Desktop, spec §8.8):

```csharp
// (license header)

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Tests.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spawns the real exe (harness: StdioProtocolCleanlinessTests), makes one failing call, and
/// checks stdout stays JSON-RPC and the log tells the story. Desktop: it spawns a process.</summary>
[Trait("Category", "Desktop")]
public sealed class ServerLogProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-proc-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task<string[]> RunAsync(params string[] serverArgs)
    {
        var psi = new ProcessStartInfo(StdioProtocolCleanlinessTests.LocateServerExe())
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in serverArgs) psi.ArgumentList.Add(a);
        psi.Environment["FLAUI_MCP_LOG_DIR"] = _dir;
        using var p = Process.Start(psi)!;
        var stdout = new System.Collections.Generic.List<string>();
        try
        {
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""");
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"desktop_focus_window","arguments":{"window":"w999"}}}""");
            await p.StandardInput.FlushAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (stdout.Count(l => l.Contains("\"id\":")) < 2)
            {
                var l = await p.StandardOutput.ReadLineAsync(cts.Token);
                if (l is null) break;
                if (l.Trim().Length > 0) stdout.Add(l);
            }
            p.StandardInput.Close();                     // clean shutdown -> server.stop
            await p.WaitForExitAsync(cts.Token);
        }
        finally { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        Assert.All(stdout, l => Assert.StartsWith("{", l.TrimStart()));
        return stdout.ToArray();
    }

    [Fact]
    public async Task One_failing_call_is_fully_correlated_in_the_log()
    {
        var stdout = await RunAsync();
        var callReply = stdout.Single(l => l.Contains("\"id\":2"));
        var segments = Directory.GetFiles(_dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var recs = JsonlLogReader.ReadSegments(segments).ToArray();
        Assert.Equal("server.start", recs.First().GetProperty("ev").GetString());
        Assert.Equal("server.stop", recs.Last().GetProperty("ev").GetString());
        var start = recs.Single(r => r.GetProperty("ev").GetString() == "tool.start");
        var end = recs.Single(r => r.GetProperty("ev").GetString() == "tool.end");
        var cid = start.GetProperty("cid").GetString()!;
        Assert.Equal(cid, end.GetProperty("cid").GetString());
        Assert.Equal("desktop_focus_window", start.GetProperty("tool").GetString());
        Assert.Equal("w999", start.GetProperty("args").GetProperty("window").GetString());  // catalog matched the SDK name
        Assert.NotEqual("ok", end.GetProperty("outcome").GetString());
        Assert.Contains($"{cid} in ", callReply);          // the error's `log` pointer names this cid
    }

    [Fact]
    public async Task No_log_flag_writes_no_file_and_no_pointer()
    {
        var stdout = await RunAsync("--no-log");
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
        Assert.DoesNotContain("\\\"log\\\"", stdout.Single(l => l.Contains("\"id\":2")));
    }
}
```

  `LocateServerExe` is `internal static` on `StdioProtocolCleanlinessTests` (same assembly), so this compiles. If the tool's error reply for an unknown handle is not an error JSON (inspect `callReply` once), STOP and report what it was.

- [ ] **Step 4:** Build; run `dotnet test FlaUI.Mcp.slnx -c Debug --no-build --filter "FullyQualifiedName~ServerLogProcessTests"` on an interactive desktop → 2 passed.
- [ ] **Step 5:** Commit `test(logging): no content on disk, and the real server's log end to end`.

---

### Task 15: Documentation

**Files:** `docs/agent-contract.md` (LF), `docs/operator-manual.md` (CRLF), `CHANGELOG.md` (CRLF), `TODO.md` (CRLF).

- [ ] **Step 1: `docs/agent-contract.md`** — add a `## Server log` section immediately before `## Known Tool Limitations` (line 268). Content (write it fully; terse, agent-facing):
  - Location: `%LOCALAPPDATA%\flaui-mcp\logs\flaui-mcp-<pid>-<startUtc yyyyMMddTHHmmssZ>-<seg>.jsonl` (or `FLAUI_MCP_LOG_DIR`); read segments in ascending NUMERIC `<seg>` order (at least two digits; past 99 a plain name sort puts `-100` before `-99`); skip a final torn line of the newest segment.
  - Every tool error JSON may carry `log: "<cid> in <path>"`: open that file and filter by `cid`.
  - The record table (spec §5 field table) and the event table (spec §5.2 with the plan's adjusted field sets), and the masked-value object `{len, sha8}` (equal values within one session hash equal).
  - What is never in the log: typed/pasted/set/clipboard text, captured screen text, images, tool results.
- [ ] **Step 2: `docs/operator-manual.md`** — add `--no-log` and `--log-level=debug|info|warning` to `## CLI reference` (line 89) in that section's existing format; add `FLAUI_MCP_LOG_DIR` to the `## Environment variables` table (lines 126-138) as a row in the same column shape (default `%LOCALAPPDATA%\flaui-mcp\logs`); under `## Watch & audit the agent` (line 231) add a short `### Server log` subsection: on by default, retention (5 MB × 5 per process, newest 20 files of ended processes), and the privacy rule (content hashed with a per-process key; UI text of sensitive windows hashed; with `--redaction-rules` loaded, UI text of calls without a target window is hashed). Keep CRLF.
- [ ] **Step 3: `CHANGELOG.md`** — under `## [Unreleased]`, add `### Added` above `### Fixed` with one entry: **Structured server log for post-mortem diagnosis.** (2-4 lines: JSONL, on by default, `--no-log`, error `log` pointer, no user content on disk.) Keep CRLF.
- [ ] **Step 4: `TODO.md`** — tick the "Structured logging in the flaui-mcp server" item under `### Unscheduled` (`- [ ]` → `- [x]`) and append ` *(shipped: see docs/agent-contract.md "Server log")*` to its first line. Keep CRLF.
- [ ] **Step 5:** Check CRLF counts are unchanged in kind (pwsh `[IO.File]::ReadAllText`, CR count > 0 for the CRLF files, 0 for agent-contract.md). Run `just lint-scripts` is NOT needed (no `.ps1` changed).
- [ ] **Step 6:** Commit `docs: the structured server log (agent contract, operator manual, changelog)`.

---

### Task 16: Final gates

- [ ] **Step 1:** `dotnet build FlaUI.Mcp.slnx -c Release` → `Build succeeded.`
- [ ] **Step 2:** Headless gate (Conventions) → baseline + all new headless tests, 0 failed, 0 skipped beyond baseline.
- [ ] **Step 3:** Desktop logging tests on an interactive console: `--filter "FullyQualifiedName~ServerLogProcessTests|FullyQualifiedName~StdioProtocolCleanlinessTests"` → all pass.
- [ ] **Step 4:** `git status --short` → empty. Report the commit list for the capstone (`21b26de..HEAD`).

---

## Stand-downs
None.

## Review ledger
- Panel r1 (agy, FINDINGS-2): Roll retention loop `break` on a gap -> `continue`; segment regex `\d{2}` -> `\d{2,}`. Driver addition while verifying: numeric (not ordinal) segment order past 99, documented in Task 15. agy's "compiles cleanly" is a reading claim, not a build: the first build of each task is the real check.

## Self-review (done while writing)

- **Spec coverage:** §4.1 → Task 5; §4.2 → Task 8; §4.3 → Task 6; §4.4 → Task 2; §4.5 → Task 9; §4.6 → Tasks 11-13; §5/§5.1 → Task 4; §5.2 events → Tasks 10-13 (server.*, window.bind, ref.resolve, selector.resolve, action.blocked, focus.attempt, input.decision, wait.end, capture.decision, tool.*, log.dropped); §5.3 → Task 5 `JsonlLogReader`; §5.4 → Task 10; §5.5 → Task 9; §6.1/§6.2 → Tasks 6-7; §6.3 → Tasks 3, 8; §6.4 → Task 3; §7 → Tasks 5, 8, 9, 10; §8.1-8.8 → Tasks 3-14; §9 → Task 15.
- **Placeholders:** none. `IWindowImageSource`, `FindQuery`, `KeyType`'s action and the test fakes were resolved against the code while writing. One "copy the exact text first" check remains on purpose (the WaitCoordinator public signatures, whose originals win): it names the lines and the STOP rule.
- **Type consistency:** `LogScope(Cid, Tool, Sensitive, HasContentArgs, Targetless)` everywhere; `ToolCallLogFilter.RunAsync(string, IDictionary<string, JsonElement>?, Func<ValueTask<CallToolResult>>)` in Tasks 8, 10, 14; `ICallSensitivity.Decide` returns `(bool Sensitive, bool Targetless)` in Tasks 8 and 14.
