# SP-B — User-State Presence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose a coarse, opt-in, privacy-preserving activity signal (`active`/`nearby`/`away`) via a read-only `desktop_user_state` tool so an agent (`/autogoal`) can escalate how it signals a human — the server is a dumb sensor; the agent derives watching/working/nearby/away and orchestrates channels.

**Architecture:** A pure Core bucketer (`IdleActivity`) turns rollover-safe idle-ms into a coarse enum; a `IIdleSource` seam (Win32 `GetLastInputInfo` behind it) keeps the bucketer headless-testable. A live-checked presence-state file (mirroring the file-based lease) gates the sensor so `presence off` revokes *immediately* (not on reconnect). The tool always returns the same unified shape `{ enabled, activity }`.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), xUnit (`Category!=Desktop` headless gate), Win32 P/Invoke (`GetLastInputInfo`, `GetTickCount`), ModelContextProtocol SDK tools.

**Reference — source spec:** `docs/superpowers/specs/2026-07-05-flaui-mcp-user-state-presence-design.md`.

**Dependency on SP-A + build ordering (SEAT-I fold — mandatory):** SP-B reuses `ConfigArgsMerge` (introduced in **SP-A Task 9**). Both plans edit the SAME four files (`ServerOptions.cs`, `Install/CliRouter.cs`, `Program.cs`, `Install/ConfigArgsMerge.cs`), so they MUST run **strictly sequentially — SP-A fully, then SP-B** — NEVER as parallel subagents (guaranteed merge conflicts / clobbered edits across those files). Recommended order: complete SP-A, then execute SP-B. If SP-A has not been implemented when SP-B is built, Task 5 below builds `ConfigArgsMerge` first (small and self-contained — see the note in Task 5). Otherwise Task 5 state-verifies it exists and reuses it. When appending `ServerOptions` params / `FromArgs` lines / `Program.cs` DI singletons, APPEND after SP-A's additions (do not assume the SP-A-baseline line shape — state-verify the current signature first).

---

## Plan-level constants (the spec deferred these to plan; pinned here)

| Constant | Value | Where | Spec ref |
|---|---|---|---|
| Default X (active→nearby) | **60 s** | `presence on --nearby-secs` default | §3.1 |
| Default Y (nearby→away) | **300 s** | `presence on --away-secs` default | §3.1 |
| `Y > X` enforced | invalid → refuse the verb | `CliRouter` presence case | §3.1 |
| Never raw idle-ms | only the coarse enum leaves the process | `desktop_user_state` | §3.1/§4 |

## File structure

- Create `src/FlaUI.Mcp.Core/Presence/IdleActivity.cs` — pure idle→enum bucketing + `Y>X` validation.
- Create `src/FlaUI.Mcp.Core/Presence/IIdleSource.cs` + `Win32IdleSource.cs` — the `GetLastInputInfo` seam + Win32 impl + rollover-safe `IdleMath`.
- Create `src/FlaUI.Mcp.Core/Presence/PresenceState.cs` — live-checked enabled-state file reader (mirror `FileLeaseProvider`) + `PresenceConfig`.
- Create `src/FlaUI.Mcp.Server/Presence/PresenceStateWriter.cs` — writes/removes the state file (mirror `LeaseWriter`).
- Modify `src/FlaUI.Mcp.Server/ServerOptions.cs` — add `Presence` flag + `NearbySecs`/`AwaySecs`.
- Create `src/FlaUI.Mcp.Server/Tools/PresenceTools.cs` — the `desktop_user_state` tool (unified shape).
- Modify `src/FlaUI.Mcp.Server/Program.cs` — DI-bind the presence stack.
- Modify `src/FlaUI.Mcp.Server/Install/CliRouter.cs` + `McpServerEntry.cs` — `presence on|off` verb (reuses `ConfigArgsMerge`).
- Docs: `README.md`, `CHANGELOG.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`, update SP-A spec §9.3 pointer, version bump.

## Build / test commands (RESPECT THE REPO GATE AS-IS)

- Build: `dotnet build -clp:ErrorsOnly` → `0 Warning(s) 0 Error(s)` (5 projects).
- Headless: `dotnet test --filter "Category!=Desktop" --no-build` → `Failed: 0`.
- Desktop (`Category=Desktop`, console-only): real `GetLastInputInfo` transitions active→nearby→away as the console sits idle; validated at the console smoke.

---

### Task 1: `IdleActivity` — idle→enum bucketing + `Y>X` validation

**Files:**
- Create: `src/FlaUI.Mcp.Core/Presence/IdleActivity.cs`
- Test: `test/FlaUI.Mcp.Tests/Presence/IdleActivityTests.cs`

**Oracle:** spec §3.1 — buckets: `active` (idle < X), `nearby` (X ≤ idle < Y), `away` (idle ≥ Y); `Y > X` enforced.

**Context (Step 0):** No existing presence code (grep to confirm). This task takes the **already-computed idle-ms** (a non-negative long the seam produces rollover-safe — Task 2) so the bucketer is pure; it guards a negative input defensively.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class IdleActivityTests
{
    [Theory]
    [InlineData(0, Activity.Active)]
    [InlineData(59_999, Activity.Active)]
    [InlineData(60_000, Activity.Nearby)]   // X boundary → nearby
    [InlineData(299_999, Activity.Nearby)]
    [InlineData(300_000, Activity.Away)]     // Y boundary → away
    [InlineData(10_000_000, Activity.Away)]
    public void Buckets_idle_ms(long idleMs, Activity expected)
        => Assert.Equal(expected, IdleActivity.Bucket(idleMs, nearbyMs: 60_000, awayMs: 300_000));

    [Fact]
    public void Negative_idle_is_clamped_to_active()   // a bad seam read must not read as away
        => Assert.Equal(Activity.Active, IdleActivity.Bucket(-5, 60_000, 300_000));

    [Theory]
    [InlineData(60, 300, true)]
    [InlineData(300, 60, false)]   // Y < X → invalid
    [InlineData(60, 60, false)]    // Y == X → invalid (must be strictly greater)
    public void Validates_Y_greater_than_X(int x, int y, bool valid)
        => Assert.Equal(valid, IdleActivity.IsValidThresholds(x, y));
}
```

- [ ] **Step 2: Run test to verify it fails** — `dotnet test --filter "FullyQualifiedName~IdleActivityTests" --no-build` — Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace FlaUI.Mcp.Core.Presence;

/// <summary>Coarse presence buckets (spec §3.1) — the ONLY activity information that leaves the process.
/// Raw idle-ms is never exposed (privacy: it is a behavioral-biometric stream).</summary>
public enum Activity { Active, Nearby, Away }

/// <summary>Pure idle-ms → coarse-enum bucketer + threshold validation. Rollover-safety lives in the seam
/// that produces idleMs (Task 2); this stays pure/headless.</summary>
public static class IdleActivity
{
    public static Activity Bucket(long idleMs, long nearbyMs, long awayMs)
    {
        if (idleMs < nearbyMs) return Activity.Active;      // includes the defensive negative case
        if (idleMs < awayMs) return Activity.Nearby;
        return Activity.Away;
    }

    /// <summary>Y (away) must be strictly greater than X (nearby); both positive.</summary>
    public static bool IsValidThresholds(int nearbySecs, int awaySecs)
        => nearbySecs > 0 && awaySecs > nearbySecs;
}
```

- [ ] **Step 4: Run test to verify it passes** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~IdleActivityTests" --no-build` — Expected: PASS (10/10).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Presence/IdleActivity.cs test/FlaUI.Mcp.Tests/Presence/IdleActivityTests.cs
git commit -m "feat(presence): IdleActivity coarse idle→enum bucketing + Y>X validation (SP-B T1)"
```

---

### Task 2: `IIdleSource` seam + `Win32IdleSource` (rollover-safe `GetLastInputInfo`)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Presence/IIdleSource.cs`
- Create: `src/FlaUI.Mcp.Core/Presence/Win32IdleSource.cs`
- Test: `test/FlaUI.Mcp.Tests/Presence/IdleRolloverTests.cs`

**Oracle:** spec §3.1 — rollover-safe math (`GetLastInputInfo.dwTime` is a 32-bit tick wrapping ~49.7 days; a naive widen-then-subtract underflows at the wrap and jams the sensor). Pin a near-wrap test.

**Context (Step 0):** Confirm no `GetLastInputInfo` P/Invoke exists yet (grep). The seam returns idle-ms as a non-negative long; the rollover-safe subtraction is itself pure (`IdleMath.Compute(now32, last32)` using `unchecked` 32-bit wraparound). `GetTickCount()` and `dwTime` are the SAME 32-bit clock — so `unchecked` subtraction is exact across a wrap; `GetTickCount64` is deliberately NOT used (it would mismatch the 32-bit `dwTime`).

- [ ] **Step 1: Write the failing rollover test**

```csharp
using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class IdleRolloverTests
{
    [Fact]
    public void Normal_case_now_after_last()
        => Assert.Equal(5000u, IdleMath.Compute(now: 10_000, last: 5_000));

    [Fact]
    public void Wrap_case_now_wrapped_past_uint_max()
    {
        // last input just before the 32-bit wrap; now just after it. A naive widened signed (now - last)
        // underflows to ~4.29e9; unchecked 32-bit wraparound gives the true small idle.
        uint last = uint.MaxValue - 1000; // 1000 ticks before wrap
        uint now = 2000;                   // 2000 ticks after wrap
        Assert.Equal(3000u, IdleMath.Compute(now, last)); // 1000 + 2000 = 3000 ms
    }

    [Fact]
    public void Equal_ticks_zero_idle()
        => Assert.Equal(0u, IdleMath.Compute(42, 42));
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL (`IdleMath` missing).

- [ ] **Step 3: Implement the seam + rollover-safe math + Win32 impl**

`IIdleSource.cs`:
```csharp
namespace FlaUI.Mcp.Core.Presence;

/// <summary>Seam over the OS last-input clock. Returns milliseconds since the last physical input as a
/// non-negative long (rollover already resolved). A fake supplies the number in headless tests — the real
/// Win32 GetLastInputInfo is NEVER called in a unit test.</summary>
public interface IIdleSource
{
    long IdleMs();
}

/// <summary>Pure rollover-safe idle computation over 32-bit tick counts (spec §3.1). `unchecked` makes the
/// subtraction wrap correctly across the ~49.7-day GetTickCount boundary.</summary>
public static class IdleMath
{
    public static uint Compute(uint now, uint last) => unchecked(now - last);
}
```

`Win32IdleSource.cs`:
```csharp
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core.Presence;

/// <summary>Real IIdleSource. GetLastInputInfo.dwTime and GetTickCount() are the same 32-bit clock;
/// IdleMath.Compute does the wrap-safe subtraction. Local, read-only — no synthetic input, no network.</summary>
public sealed class Win32IdleSource : IIdleSource
{
    public long IdleMs()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0; // fail-soft to "active" (never falsely report away)
        return IdleMath.Compute(GetTickCount(), lii.dwTime);
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();
}
```

- [ ] **Step 4: Run test to verify it passes** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~IdleRolloverTests" --no-build` — Expected: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Presence/IIdleSource.cs src/FlaUI.Mcp.Core/Presence/Win32IdleSource.cs test/FlaUI.Mcp.Tests/Presence/IdleRolloverTests.cs
git commit -m "feat(presence): IIdleSource seam + rollover-safe GetLastInputInfo (SP-B T2)"
```

---

### Task 3: `PresenceState` — live-checked enabled-state file (immediate off)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Presence/PresenceState.cs` (live reader + `PresenceConfig`)
- Create: `src/FlaUI.Mcp.Server/Presence/PresenceStateWriter.cs` (CLI writer)
- Test: `test/FlaUI.Mcp.Tests/Presence/PresenceStateTests.cs`

**Oracle:** spec §3.2 — turning presence *off* is a privacy-consent REVOCATION and must take effect NOW, not on reconnect. The running server reads the enabled-state LIVE per query from a small state file the CLI writes (mirroring the file-based lease). `--presence` launch flag sets the *default*; the file is the live override.

**Context (Step 0):** Open `src/FlaUI.Mcp.Core/Interaction/FileLeaseProvider.cs` — mirror `LeaseDir()` (`%LOCALAPPDATA%\FlaUI.Mcp`, overridable via `FLAUI_MCP_DATA_DIR`), the `FileShare.ReadWrite` + short-retry read, and `LeaseWriter.Grant/Revoke`. The presence file is simpler (no SID/expiry). **File shape (synthetic example):** one line `enabled=1;nearbySecs=60;awaySecs=300` at `%LOCALAPPDATA%\FlaUI.Mcp\presence.state`.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class PresenceStateTests
{
    [Fact]
    public void Round_trips_enabled_and_thresholds()
    {
        var line = PresenceConfig.Format(new PresenceConfig(true, 60, 300));
        Assert.True(PresenceConfig.TryParse(line, out var c));
        Assert.True(c.Enabled);
        Assert.Equal(60, c.NearbySecs);
        Assert.Equal(300, c.AwaySecs);
    }

    [Fact]
    public void Off_line_parses_as_disabled()
    {
        Assert.True(PresenceConfig.TryParse("enabled=0;nearbySecs=60;awaySecs=300", out var c));
        Assert.False(c.Enabled);
    }

    [Fact]
    public void Garbage_fails_to_parse()
        => Assert.False(PresenceConfig.TryParse("not-a-line", out _));
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 3: Implement `PresenceConfig` + `PresenceState` reader**

`PresenceState.cs` (Core):
```csharp
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FlaUI.Mcp.Core.Presence;

/// <summary>The live presence config: enabled + thresholds. One line, key=value;… .</summary>
public readonly record struct PresenceConfig(bool Enabled, int NearbySecs, int AwaySecs)
{
    public static string Format(PresenceConfig c) =>
        $"enabled={(c.Enabled ? 1 : 0)};nearbySecs={c.NearbySecs};awaySecs={c.AwaySecs}";

    public static bool TryParse(string? line, out PresenceConfig cfg)
    {
        cfg = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        bool? enabled = null; int? near = null, away = null;
        foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) return false;
            switch (kv[0].Trim())
            {
                case "enabled": enabled = kv[1].Trim() == "1"; break;
                case "nearbySecs": if (int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) near = n; break;
                case "awaySecs": if (int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)) away = a; break;
            }
        }
        if (enabled is null || near is null || away is null) return false;
        cfg = new PresenceConfig(enabled.Value, near.Value, away.Value);
        return true;
    }
}

/// <summary>Live per-query reader of the presence state file (spec §3.2 immediate-off). Mirrors
/// FileLeaseProvider: same data dir, FileShare.ReadWrite + short retry. Absent file → the launch default.</summary>
public sealed class PresenceState
{
    public static string StateDir()
    {
        var dir = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlaUI.Mcp");
        return dir;
    }
    public static string StatePath() => Path.Combine(StateDir(), "presence.state");

    /// <summary>Read the live config. Absent file → `launchDefault` (the --presence launch flag's config).
    /// Unreadable/garbage → disabled (fail-closed to no-telemetry).</summary>
    public PresenceConfig Read(PresenceConfig launchDefault)
    {
        var path = StatePath();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return launchDefault;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return PresenceConfig.TryParse(sr.ReadLine(), out var c) ? c : new PresenceConfig(false, 60, 300);
            }
            catch (IOException) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) { return new PresenceConfig(false, 60, 300); }
        }
        return new PresenceConfig(false, 60, 300);
    }
}
```

`PresenceStateWriter.cs` (Server, mirrors `LeaseWriter`):
```csharp
using System.IO;
using FlaUI.Mcp.Core.Presence;

namespace FlaUI.Mcp.Server.Presence;

/// <summary>Writes/removes the live presence state file for the CLI (spec §3.2). `presence off` writes an
/// explicit disabled line so a running server sees it on its NEXT query — immediate revocation, no reconnect.</summary>
public static class PresenceStateWriter
{
    public static string Set(bool enabled, int nearbySecs, int awaySecs)
    {
        Directory.CreateDirectory(PresenceState.StateDir());
        File.WriteAllText(PresenceState.StatePath(), PresenceConfig.Format(new PresenceConfig(enabled, nearbySecs, awaySecs)));
        return enabled
            ? $"presence ON (nearby {nearbySecs}s, away {awaySecs}s) — active immediately."
            : "presence OFF — telemetry stopped immediately.";
    }
}
```

- [ ] **Step 4: Run test + build** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~PresenceStateTests" --no-build` — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Presence/PresenceState.cs src/FlaUI.Mcp.Server/Presence/PresenceStateWriter.cs test/FlaUI.Mcp.Tests/Presence/PresenceStateTests.cs
git commit -m "feat(presence): live-checked presence state file (immediate off) (SP-B T3)"
```

---

### Task 4: `desktop_user_state` tool + `ServerOptions` + DI

**Files:**
- Modify: `src/FlaUI.Mcp.Server/ServerOptions.cs` (add `Presence`, `NearbySecs`, `AwaySecs`)
- Create: `src/FlaUI.Mcp.Server/Tools/PresenceTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (DI-bind `IIdleSource`, `PresenceState`, `PresenceTools`)
- Test: `test/FlaUI.Mcp.Tests/Presence/PresenceToolsTests.cs`

**Context (Step 0):** Confirm `ServerOptions` current signature. If SP-A already added `Autosound`, append after it. Baseline: `ServerOptions(bool ReadOnly, bool AllowElevation, bool Overlay = false, int OverlayMs = 500 [, bool Autosound = false])`. Confirm `ToolResponse.Ok` + the `[McpServerTool(ReadOnly = true)]` convention from an existing read-only tool (`DesktopInputStatus` in `InputTools.cs` uses `ToolResponse.Guard(() => Task.FromResult(ToolResponse.Ok(...)))` — mirror it).

**Oracle:** spec §3.1 — **unified non-polymorphic schema**: ALWAYS `{ enabled: bool, activity: "active"|"nearby"|"away"|null }`. On → `{enabled:true, activity:"…"}`; off → `{enabled:false, activity:null}`. Read-only ⇒ lease-exempt. NEVER raw idle-ms.

- [ ] **Step 1: Add `ServerOptions` fields + `ParseIntArg`**

```csharp
// append to the record parameter list (after Autosound if SP-A added it, else after OverlayMs):
bool Presence = false, int NearbySecs = 60, int AwaySecs = 300
// in FromArgs, add:
Presence: args.Contains("--presence"),
NearbySecs: ParseIntArg(args, "--nearby-secs=", 60),
AwaySecs: ParseIntArg(args, "--away-secs=", 300)
```
Add `ParseIntArg(string[] args, string prefix, int fallback)` mirroring `ParseOverlayMs` (find `args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))`, parse the suffix, fallback on absence/garbage).

- [ ] **Step 2: Write the failing test** (pure reply builder — assert the unified shape both ways, no raw ms):

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Presence;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class PresenceToolsTests
{
    [Fact]
    public void Disabled_returns_enabled_false_activity_null()
    {
        var json = PresenceTools.Reply(new PresenceConfig(false, 60, 300), idleMs: 0);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("activity").ValueKind);
    }

    [Fact]
    public void Enabled_returns_the_coarse_enum_never_raw_ms()
    {
        var json = PresenceTools.Reply(new PresenceConfig(true, 60, 300), idleMs: 120_000);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("nearby", doc.RootElement.GetProperty("activity").GetString());
        Assert.False(doc.RootElement.TryGetProperty("idleMs", out _));   // NEVER raw ms
    }

    [Fact]
    public void Shape_is_invariant_same_keys_both_states()
    {
        var on = JsonDocument.Parse(PresenceTools.Reply(new PresenceConfig(true, 60, 300), 0)).RootElement;
        var off = JsonDocument.Parse(PresenceTools.Reply(new PresenceConfig(false, 60, 300), 0)).RootElement;
        Assert.True(on.TryGetProperty("enabled", out _) && on.TryGetProperty("activity", out _));
        Assert.True(off.TryGetProperty("enabled", out _) && off.TryGetProperty("activity", out _));
    }
}
```

- [ ] **Step 3: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 4: Implement `PresenceTools`**

```csharp
using System.ComponentModel;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Presence;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class PresenceTools
{
    private readonly ServerOptions _options;
    private readonly PresenceState _state;
    private readonly IIdleSource _idle;

    public PresenceTools(ServerOptions options, PresenceState state, IIdleSource idle)
    { _options = options; _state = state; _idle = idle; }

    [McpServerTool(ReadOnly = true), Description("Report the human's COARSE presence: { enabled, activity: \"active\"|\"nearby\"|\"away\"|null }. \"active\" = recent input; \"nearby\" = idle past a short threshold; \"away\" = idle past a longer one. Read-only, lease-EXEMPT. Off by default — returns { enabled:false, activity:null } until a human runs `flaui-mcp presence on`. NEVER returns raw idle milliseconds (privacy). Combine with desktop_focus_window/desktop_wait_for_foreground to derive watching/working and escalate how you signal the human.")]
    public Task<string> DesktopUserState()
        => ToolResponse.Guard(() =>
        {
            // Launch flag sets the default; the live state file overrides it (immediate off).
            var launchDefault = new PresenceConfig(_options.Presence, _options.NearbySecs, _options.AwaySecs);
            var cfg = _state.Read(launchDefault);
            long idleMs = cfg.Enabled ? _idle.IdleMs() : 0; // don't even read the clock when disabled
            return Task.FromResult(Reply(cfg, idleMs));
        });

    /// <summary>Pure reply builder — the unified non-polymorphic shape (spec §3.1). Disabled → activity null;
    /// enabled → the coarse enum only. Never emits raw idle-ms.</summary>
    public static string Reply(PresenceConfig cfg, long idleMs)
    {
        if (!cfg.Enabled) return ToolResponse.Ok(new { enabled = false, activity = (string?)null });
        var a = IdleActivity.Bucket(idleMs, cfg.NearbySecs * 1000L, cfg.AwaySecs * 1000L);
        var s = a switch { Activity.Active => "active", Activity.Nearby => "nearby", _ => "away" };
        return ToolResponse.Ok(new { enabled = true, activity = (string?)s });
    }
}
```

- [ ] **Step 5: DI-bind in `Program.cs`**

```csharp
// --- SP-B user-state presence (coarse, opt-in, read-only) ---
builder.Services.AddSingleton<FlaUI.Mcp.Core.Presence.IIdleSource, FlaUI.Mcp.Core.Presence.Win32IdleSource>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Presence.PresenceState>();
builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.PresenceTools>();
```

- [ ] **Step 6: Build + full headless suite** — Expected: PASS. Any test constructing `ServerOptions(...)` positionally still compiles (new params have defaults — grounding: the record documents exactly this compatibility pattern).

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/ServerOptions.cs src/FlaUI.Mcp.Server/Tools/PresenceTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Presence/PresenceToolsTests.cs
git commit -m "feat(presence): desktop_user_state unified-shape tool + ServerOptions + DI (SP-B T4)"
```

---

### Task 5: `flaui-mcp presence on|off` verb (reuses `ConfigArgsMerge`)

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (`presence` verb + immediate state-file write)
- Test: extend `test/FlaUI.Mcp.Tests/Install/CliRouterTests.cs`

**Context (Step 0 — DEPENDENCY CHECK):** Confirm `src/FlaUI.Mcp.Server/Install/ConfigArgsMerge.cs` exists (SP-A Task 9). **If it does NOT exist** (SP-B built before SP-A), create it now from SP-A Task 9 Step 3 verbatim + its test `ConfigArgsMergeTests.cs` (SP-A Task 9 Step 1), and add the `ApplyMerge(agent, paths, exePath, add, remove)` helper + the per-writer merge overloads from SP-A Task 9 Steps 5-6. If it DOES exist, reuse it. STOP + `STATE_MISMATCH` if `ConfigArgsMerge.Apply` has a different signature than `(IReadOnlyList<string>? existing, IReadOnlyList<string> add, IReadOnlyList<string> removeAnyOf) → string[]`.

**Oracle:** spec §3.2 — `presence on|off` mirrors `overlay`/`autosound`; off by default; the agent can NEVER enable it (human-CLI only); `on` re-registers with `--presence` (+ thresholds) via the non-destructive merge; AND writes the live state file so `off` revokes immediately (Task 3). `Y > X` enforced — invalid → refuse.

- [ ] **Step 1: Write the failing CLI test** (extend `CliRouterTests`; follow its temp-config + `FLAUI_MCP_DATA_DIR` harness):

```csharp
// sketch — adapt to the existing CliRouterTests harness helpers:
[Fact]
public void Presence_on_registers_flag_and_writes_live_state()
{
    var rc = RunVerb("presence", "on", "--nearby-secs", "30", "--away-secs", "200", "--config", cfgPath);
    Assert.Equal(0, rc);
    var args = ReadArgs(cfgPath); // reads mcpServers.flaui-mcp.args
    Assert.Contains("--presence", args);
    Assert.Contains("--nearby-secs=30", args);
    Assert.Contains("--away-secs=200", args);
    Assert.True(PresenceConfig.TryParse(File.ReadAllText(PresenceState.StatePath()), out var c) && c.Enabled);
}

[Fact]
public void Presence_off_preserves_overlay_and_revokes_live()
{
    RunVerb("overlay", "on", "--config", cfgPath);
    RunVerb("presence", "on", "--config", cfgPath);
    RunVerb("presence", "off", "--config", cfgPath);
    var args = ReadArgs(cfgPath);
    Assert.Contains("--overlay", args);          // sibling preserved
    Assert.DoesNotContain("--presence", args);
    Assert.True(PresenceConfig.TryParse(File.ReadAllText(PresenceState.StatePath()), out var c) && !c.Enabled);
}

[Fact]
public void Presence_on_with_invalid_thresholds_refuses()
{
    var rc = RunVerb("presence", "on", "--nearby-secs", "60", "--away-secs", "10", "--config", cfgPath);
    Assert.Equal(2, rc);
}
```
(Adapt `RunVerb`/`ReadArgs` to whatever the existing `CliRouterTests` already uses to invoke `CliRouter.Run` and inspect the written config. Ensure the test sets `FLAUI_MCP_DATA_DIR` to a temp dir so `PresenceState.StatePath()` is isolated — mirror how the lease/install tests isolate the data dir.)

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 3: Add `presence` to the `Verbs` set + the case in `CliRouter`**

```csharp
case "presence":
{
    var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    if (mode != "on" && mode != "off")
    { outp.WriteLine("usage: flaui-mcp presence on|off [--nearby-secs N] [--away-secs N] [--agent ...]"); return 2; }
    int nearby = int.TryParse(OptionValue(args, "--nearby-secs"), out var nn) ? nn : 60;
    int away = int.TryParse(OptionValue(args, "--away-secs"), out var aa) ? aa : 300;
    if (mode == "on" && !FlaUI.Mcp.Core.Presence.IdleActivity.IsValidThresholds(nearby, away))
    { outp.WriteLine($"invalid thresholds: away-secs ({away}) must be greater than nearby-secs ({nearby})."); return 2; }

    // 1) Non-destructive config merge (default via the --presence launch flag group).
    var add = mode == "on"
        ? new[] { "--presence", $"--nearby-secs={nearby}", $"--away-secs={away}" }
        : System.Array.Empty<string>();
    var remove = new[] { "--presence", "--nearby-secs", "--away-secs" };
    foreach (var r in ApplyMerge(agent, paths, exePath, add, remove))
        outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");

    // 2) Live state file — makes `off` revoke NOW (no reconnect) and `on` active immediately.
    outp.WriteLine(FlaUI.Mcp.Server.Presence.PresenceStateWriter.Set(mode == "on", nearby, away));
    outp.WriteLine($"Presence {mode.ToUpperInvariant()}. The live change is immediate; the launch default applies after the next /mcp reconnect.");
    return 0;
}
```
Add `"presence"` to the `Verbs` `HashSet`. Update `PrintHelp` to document `presence on|off [--nearby-secs N] [--away-secs N]` (off by default; human-only; coarse enum; never raw idle-ms).

- [ ] **Step 4: Build + full headless suite** — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ test/FlaUI.Mcp.Tests/Install/CliRouterTests.cs
git commit -m "feat(install): presence on|off verb — merge flags + immediate live state (SP-B T5)"
```

---

### Task 6: Desktop smoke for the real idle source (console-only)

**Files:**
- Create: `test/FlaUI.Mcp.Tests/Presence/PresenceDesktopTests.cs` (`Category=Desktop`)

**Context (Step 0):** Confirm the `Category=Desktop` trait convention from an existing Desktop test (grep `Trait("Category"` in `test/`). Mark the class with the same trait so `--filter "Category!=Desktop"` excludes it from CI.

**Oracle:** spec §6 — real `GetLastInputInfo` reports `active` right after input; transitions active→nearby→away as the console sits idle (the transitions are validated by hand at the smoke, not asserted on a timer).

- [ ] **Step 1: Write the Desktop test**

```csharp
using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

[Trait("Category", "Desktop")]   // match the repo's existing Desktop trait convention
public class PresenceDesktopTests
{
    [Fact]
    public void Real_idle_source_reports_active_right_after_input()
    {
        var idle = new Win32IdleSource().IdleMs();
        Assert.True(idle >= 0);
        // Run this immediately after touching the console → recent input → active bucket.
        Assert.Equal(Activity.Active, IdleActivity.Bucket(idle, 60_000, 300_000));
    }
}
```

- [ ] **Step 2: Confirm it is excluded headlessly** — `dotnet test --filter "Category!=Desktop" --no-build` — Expected: the new test is NOT run; build green.

- [ ] **Step 3: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Presence/PresenceDesktopTests.cs
git commit -m "test(presence): Desktop smoke for real GetLastInputInfo (console-only) (SP-B T6)"
```

---

### Task 7: Docs + SP-A §9.3 pointer + version bump

**Files:**
- Modify: `README.md`, `CHANGELOG.md`, `ROADMAP.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`
- Modify: `docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md` (§9.3 note → point to SP-B)
- Modify: the version-source file.

**Context (Step 0):** Grep the current version. If SP-A shipped 0.12.0, SP-B is **0.13.0** (or a combined single release if SP-A + SP-B land together — confirm the release cadence with the driver; default to a separate 0.13.0 if SP-A already tagged). **README-before-push gate** applies.

**Oracle:** spec §7 — SP-A §9.3 "dropped `userIdleMs`" note should be updated to point HERE (reconsidered: coarse enum, opt-in, agent-orchestrated) rather than read as an absolute drop.

- [ ] **Step 1:** Bump the version property (e.g. `0.12.0 → 0.13.0`).
- [ ] **Step 2:** `CHANGELOG.md` — dated entry: new read-only `desktop_user_state` tool (coarse `active/nearby/away`, off by default, never raw idle-ms); new `flaui-mcp presence on|off [--nearby-secs N] [--away-secs N]` (human-only, immediate off via a live state file).
- [ ] **Step 3:** `README.md` — add `desktop_user_state` to the tools list + a `presence on|off` note (off by default; coarse enum; no raw idle-ms; the agent orchestrates escalation).
- [ ] **Step 4:** `.claude/skills/driving-flaui-mcp/SKILL.md` — document how an agent combines `desktop_user_state` + SP-A focus to derive watching/working/nearby/away (informative). Version-agnostic.
- [ ] **Step 5:** Edit SP-A spec §9.3: change the "DROPPED" note to "reconsidered in SP-B (coarse enum, opt-in, agent-orchestrated) — see `2026-07-05-flaui-mcp-user-state-presence-design.md`".
- [ ] **Step 6:** `ROADMAP.md` — add the SP-B entry.
- [ ] **Step 7:** Final full build + headless suite — Expected: `0 Warning(s) 0 Error(s)`, `Failed: 0`.
- [ ] **Step 8: Commit**

```bash
git add README.md CHANGELOG.md ROADMAP.md .claude/skills/driving-flaui-mcp/SKILL.md docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md <version-file>
git commit -m "docs+release: SP-B user-state presence — desktop_user_state, presence verb (SP-B T7)"
```

---

## Self-review (author) — spec coverage

- §3.1 coarse sensor (active/nearby/away), rollover-safe, `Y>X`, never raw idle-ms, unified `{enabled,activity}` shape → T1 (bucket + validate) + T2 (rollover seam) + T4 (tool shape).
- §3.2 `presence on|off` (off default, human-only, non-destructive merge, **immediate off** via live state file, reconnect for launch default) → T3 (state file) + T5 (verb).
- §4 privacy/consent/airgap (coarse enum, opt-in, no server outbound, dumb sensor) → enforced by T1/T4 (no raw ms) + T5 (human-only verb); no outbound anywhere in SP-B.
- §6 testing (headless bucketing/validation/gating pure; Desktop real transitions) → T1/T2/T3/T4 headless + T6 Desktop.
- §7 relationship to SP-A (reconsidered drop) → T7 (§9.3 pointer).

**Type consistency:** `PresenceConfig`/`Activity` (T1/T3) used unchanged in T4/T5; `IIdleSource.IdleMs():long` consumed by `PresenceTools` (T4) and the Desktop test (T6); `ConfigArgsMerge.Apply` reused from SP-A T9 with the identical signature; `IdleActivity.IsValidThresholds` used in both T1 (unit) and T5 (CLI refuse).

**Cross-plan dependency:** `ConfigArgsMerge` + `CliRouter.ApplyMerge` are owned by SP-A T9; T5 reuses them (or builds `ConfigArgsMerge` from SP-A T9's spec if SP-B lands first).

## Execution handoff

Two options: **(1) Subagent-Driven (recommended)**; **(2) Inline**. Model tiering: T1–T4 are well-specified pure/contained (cheaper model OK); T5 touches the CLI + config writers + live state (standard model); T6/T7 mechanical. Desktop smoke (real active→nearby→away transitions; `presence off` immediate) runs at the console after T7.
