# Bundled Driving-Skill Distribution — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the `driving-flaui-mcp` skill to Claude Code users from the installer, in lockstep with the binary, and reversibly neutralise the colliding v0.14.x marketplace copy.

**Architecture:** The installer gains a Claude-side skill deployer that mirrors the agy path's *policy* (deploy alongside registration; a deploy failure is a warning, never a denial) but not its code — `AgyConfigWriter` writes an agy-shaped manifest, and `ClaudeCodeConfigWriter` has no file I/O at all today. Collision handling reads Claude's state via `claude plugin list --json`, disables each colliding entry **at its own scope and from its own `projectPath`**, records what it disabled in a marker outside the purge path, and restores symmetrically on uninstall. Uninstall-time warnings are handed to the Inno uninstaller, which is the only actor that outlives the exe it deletes.

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.19041.0`), xUnit, Inno Setup 6 (Pascal), PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-07-16-skill-distribution-design.md` (status: UNBLOCKED; 7 panel rounds; 34 findings, 30 valid).

**Plan review:** 1 agy panel — REJECT, 7 findings: **5 valid and folded, 2 rejected on measurement.**
Folding one of them exposed an eighth defect the panel did not reach. See
[Plan review ledger](#plan-review-ledger).

---

## Read this before Task 1

**The oracle for every task is the test named in that task.** If a value looks wrong, the test wins — surface the conflict, do not edit the test to match your code. One exception is explicit and scoped: **Task 1 deliberately changes a contract**, so it rewrites the four existing tests in `test/FlaUI.Mcp.Tests/Install/ClaudeCodeConfigWriterTests.cs`. Those rewrites are given in full. No other task may modify an existing test.

**Shape-divergence stop.** If making something compile would change the shape, type, or encoding of any value written here — even trivially — STOP and report `[original] -> [yours] because <reason>`. The marker file and `--json` parsing are wire contracts.

**State verification (Step 0 of every task).** Open the cited files and confirm the quoted code matches before editing. If it differs, STOP and report `STATE_MISMATCH: <what>`.

**Build/test commands** (the repo's real gate — do not invent stricter flags):

```bash
dotnet build -c Debug                                    # expect: 0 Warning(s), 0 Error(s)
dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"   # expect: Passed! - Failed: 0, Passed: 507+
```

Baseline at plan time: **0 warnings, 507 headless tests passing.** Every task must leave both green.

### Two facts that decide the design (measured 2026-07-16 — do not re-derive)

1. **`claude plugin list --json` is a GLOBAL inventory.** Identical output from any CWD; the store is `~/.claude/plugins/installed_plugins.json`, keyed by plugin id. Entries carry `{id, version, scope, enabled, installPath, installedAt, lastUpdated}` **plus `projectPath` on non-user-scope entries**.
2. **`claude plugin disable` is CWD-bound.** `--help` exposes only `-a/--all`, `-h/--help`, `-s/--scope <user|project|local>`. There is **no flag to target another project**. `--scope local` writes the **current directory's** `.claude/settings.local.json`. So a disable must run with its working directory set to that entry's `projectPath`, or it silently disables nothing and litters a stray settings file.

### Unknowns this plan must MEASURE, not assume

| # | Unknown | Measured in |
|---|---|---|
| M1 | Is `claude plugin disable --scope user` non-interactive? | Task 2 |
| M2 | Does Inno's `UninstallSilent` exist and behave as assumed? | Task 11 |
| M3 | Does `--json` list `skills-dir` plugins? (prior check was **vacuous**, not negative) | Task 12 |

**None of these may be assumed.** If a measurement contradicts this plan, STOP and report it — it is a design fork, not an implementation detail.

---

## File Structure

| File | Responsibility |
|---|---|
| **Create** `src/FlaUI.Mcp.Server/Install/ProcessRunner.cs` | `RunResult` + the bounded-timeout process runner. One job: run a process, never hang. |
| **Modify** `src/FlaUI.Mcp.Server/Install/ClaudeCodeConfigWriter.cs` | Widen the injected runner to `(file, args, cwd) -> RunResult`. |
| **Create** `src/FlaUI.Mcp.Server/Install/ClaudePluginInventory.cs` | Parse `claude plugin list --json` into records. Pure; never throws. |
| **Create** `src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs` | Write/remove `<claude-config>/skills/flaui-mcp/`. Never throws. |
| **Create** `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs` | Durable record of *which entries at which scopes and project paths* we disabled. Never throws. |
| **Create** `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` | Detect → disable → record; and restore → consume marker. |
| **Create** `src/FlaUI.Mcp.Server/Install/UninstallWarnings.cs` | Durable warnings file for the Inno uninstaller to surface. |
| **Modify** `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | Path resolution (`ClaudeConfigDir`, `StateDir`), wiring, uninstall ordering. |
| **Modify** `src/FlaUI.Mcp.Server/Install/InstallStatus.cs` | Report the Claude skill + collision state. |
| **Modify** `installer/flaui-mcp.iss` | `usPostUninstall` warnings dialog; version bump. |
| **Create** `scripts/install-smoke.ps1` | Local/manual gate. **CI cannot run this** (no `claude` CLI, no `~/.claude`). |
| **Modify** `docs/ops-manual.md`, `README.md` | Rewrite the falsified contract + install path. |

**Why `ProcessRunner` is its own file:** the timeout is the fix for a latent Setup-hang, and it must be testable without going through `ClaudeCodeConfigWriter`. Keeping it separate lets Task 1 test the kill path directly.

**Where state lives, and why it is not `<data-dir>`:**

| Path | Deleted by | Therefore |
|---|---|---|
| `{app}` = `%LOCALAPPDATA%\Programs\FlaUI.Mcp` | Inno `[Files]` (`iss:25`), **both** uninstall branches | cannot hold the marker |
| `<data-dir>` = `%USERPROFILE%\.flaui-mcp` | `--purge-data` (`CliRouter.cs:110-114`), which `iss:44-45` runs on the *purge* branch | cannot hold the marker (spec R4) |
| **`<state-dir>` = `%LOCALAPPDATA%\FlaUI.Mcp\state`** | nothing automatic; reaped by Task 11 | **holds the marker + warnings** |

Override for tests: `FLAUI_MCP_STATE_DIR`.

---

### Task 1: Bounded process runner, widened on two axes

**Why:** `ClaudeCodeConfigWriter.cs:62` calls `p.WaitForExit()` with **no timeout** on a process started with `CreateNoWindow = true` (`:58`). Today's `mcp add/remove` never prompt, so it has never bitten. This plan adds `claude plugin disable`, whose interactivity is **unmeasured** (M1) — if it ever prompts, the hidden process blocks on stdin forever, **Setup hangs, and the user never gets the product**. That is worse than the collision we are fixing. And per the CWD-binding fact, the runner must also carry a working directory.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ProcessRunner.cs`
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeCodeConfigWriter.cs:13-16`, `:54-66`
- Modify (contract change — authorised): `test/FlaUI.Mcp.Tests/Install/ClaudeCodeConfigWriterTests.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/ProcessRunnerTests.cs`

- [ ] **Step 0: Verify state**

Confirm `ClaudeCodeConfigWriter.cs:13` reads exactly `private readonly Func<string, string[], int> _run;` and `:62` reads `p.WaitForExit();`. If not, STOP: `STATE_MISMATCH`.

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/ProcessRunnerTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ProcessRunnerTests
{
    [Fact]
    public void Captures_stdout_and_exit_code()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo", "hello" }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.Contains("hello", r.Output);
    }

    [Fact]
    public void Reports_the_process_exit_code()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "exit", "3" }, null, TimeSpan.FromSeconds(30));
        Assert.Equal(3, r.Code);
    }

    [Fact]
    public void Missing_executable_reports_NotFound_minus_one()
    {
        var r = ProcessRunner.Run("definitely-not-a-real-exe-xyzzy", Array.Empty<string>(), null, TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessRunner.NotFound, r.Code);
    }

    // THE HAZARD THIS FILE EXISTS FOR. A hidden process that blocks on stdin would hang Setup
    // forever, and the user would never get the product. The runner must kill it and move on.
    [Fact]
    public void A_hung_process_is_killed_and_reports_TimedOut_rather_than_blocking()
    {
        var sw = Stopwatch.StartNew();

        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "pause" }, null, TimeSpan.FromSeconds(2));

        sw.Stop();
        Assert.Equal(ProcessRunner.TimedOut, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"runner blocked for {sw.Elapsed} — the timeout did not fire");
    }

    [Fact]
    public void Runs_in_the_given_working_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-cwd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "cd" }, dir, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.Contains(Path.GetFileName(dir), r.Output);
    }

    [Fact]
    public void A_null_working_directory_inherits_the_current_one()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "cd" }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.Contains(Path.GetFileName(Directory.GetCurrentDirectory()), r.Output);
    }

    // On failure the REASON is usually on stderr; a warning that says only "exited 1" is useless to
    // the user who has to fix it.
    [Fact]
    public void On_failure_stderr_is_kept_so_the_reason_survives()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo boom 1>&2 && exit 4" }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(4, r.Code);
        Assert.Contains("boom", r.Output);
    }

    // ...but on SUCCESS the output must stay clean: `claude plugin list --json` output is parsed,
    // and a stray stderr line (a deprecation notice, say) would corrupt the JSON.
    [Fact]
    public void On_success_stderr_is_excluded_so_json_output_stays_parseable()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo warning: noisy 1>&2 && echo [] " }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("noisy", r.Output);
        Assert.Contains("[]", r.Output);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ProcessRunnerTests"`
Expected: FAIL — `The type or namespace name 'ProcessRunner' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Create `src/FlaUI.Mcp.Server/Install/ProcessRunner.cs`:

```csharp
using System.Diagnostics;
using System.Text;

namespace FlaUI.Mcp.Server.Install;

/// <summary>Outcome of a child process: its exit code plus whatever it wrote to stdout.</summary>
public readonly record struct RunResult(int Code, string Output);

/// <summary>
/// Runs a child process with a BOUNDED wait. Setup launches us `runhidden`
/// (installer/flaui-mcp.iss), so a child that blocks on stdin has no window, no console, and no
/// human — it would hang Setup forever and the user would never get the product. Nothing may wait
/// on a hidden child without a deadline.
/// </summary>
public static class ProcessRunner
{
    /// <summary>The executable was not found on PATH.</summary>
    public const int NotFound = -1;
    /// <summary>The child exceeded its deadline and was killed.</summary>
    public const int TimedOut = -2;

    /// <summary>Generous enough for a slow cold start, short enough that Setup always finishes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static RunResult Run(string file, string[] args, string? workingDirectory, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;

            using var p = Process.Start(psi);
            if (p is null) return new RunResult(NotFound, "");

            // Read asynchronously: a child that fills the stdout pipe while we block on WaitForExit
            // would deadlock, which is the same hang by another route. Both streams are drained for
            // that reason. stderr is KEPT rather than discarded: when `claude` fails, its reason is
            // usually on stderr, and our warning would otherwise say only "exited 1".
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new RunResult(TimedOut, stdout.ToString());
            }
            p.WaitForExit();   // flush the async output handlers (see WaitForExit(int) remarks)

            // On success the caller wants clean stdout (it may be JSON). On failure it wants the
            // reason, which lives on stderr.
            return p.ExitCode == 0
                ? new RunResult(0, stdout.ToString())
                : new RunResult(p.ExitCode, (stdout.ToString() + stderr.ToString()).Trim());
        }
        catch (System.ComponentModel.Win32Exception) { return new RunResult(NotFound, ""); }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ProcessRunnerTests"`
Expected: PASS — 8 passed.

- [ ] **Step 5: Rewrite the four existing writer tests for the new contract**

This is the one authorised existing-test change. Replace the whole body of `test/FlaUI.Mcp.Tests/Install/ClaudeCodeConfigWriterTests.cs`. The **asserted arguments are unchanged** — only the runner's shape moves, so these keep pinning the exact same CLI contract, plus the new cwd expectation:

```csharp
using System.Collections.Generic;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCodeConfigWriterTests
{
    [Fact]
    public void Install_invokes_claude_mcp_remove_then_add_with_the_exe()
    {
        // Install is remove-then-add so re-registration is idempotent and can change the args of
        // an existing entry (`claude mcp add` fails on a duplicate name). The remove is best-effort.
        var calls = new List<(string file, string[] args, string? cwd)>();
        var w = new ClaudeCodeConfigWriter((file, args, cwd) => { calls.Add((file, args, cwd)); return new RunResult(0, ""); });

        var r = w.Install(@"C:\x\flaui-mcp.exe");

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(2, calls.Count);
        Assert.Equal("claude", calls[0].file);
        Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "flaui-mcp" }, calls[0].args);
        Assert.Equal("claude", calls[1].file);
        Assert.Equal(new[] { "mcp", "add", "--scope", "user", "flaui-mcp", "--", @"C:\x\flaui-mcp.exe" }, calls[1].args);
    }

    [Fact]
    public void Install_with_args_appends_them_to_the_add_command()
    {
        var calls = new List<(string file, string[] args, string? cwd)>();
        var w = new ClaudeCodeConfigWriter((file, args, cwd) => { calls.Add((file, args, cwd)); return new RunResult(0, ""); });

        var r = w.Install(@"C:\x\flaui-mcp.exe", new[] { "--overlay", "--overlay-ms=800" });

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(2, calls.Count);
        Assert.Equal(
            new[] { "mcp", "add", "--scope", "user", "flaui-mcp", "--", @"C:\x\flaui-mcp.exe", "--overlay", "--overlay-ms=800" },
            calls[1].args);
    }

    [Fact]
    public void Uninstall_invokes_claude_mcp_remove()
    {
        var calls = new List<string[]>();
        var w = new ClaudeCodeConfigWriter((_, args, _) => { calls.Add(args); return new RunResult(0, ""); });
        w.Uninstall();
        Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "flaui-mcp" }, Assert.Single(calls));
    }

    [Fact]
    public void Install_reports_NotFound_when_runner_signals_missing_cli()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(ProcessRunner.NotFound, ""));
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.Equal(AgentChange.NotFound, r.Change);
    }

    // The mcp verbs are global (--scope user) and must NOT inherit Setup's working directory as a
    // hidden input. Only the plugin-disable path (Task 6) is CWD-bound, and it passes one explicitly.
    [Fact]
    public void The_mcp_verbs_pass_no_working_directory()
    {
        var cwds = new List<string?>();
        var w = new ClaudeCodeConfigWriter((_, _, cwd) => { cwds.Add(cwd); return new RunResult(0, ""); });

        w.Install(@"C:\x\flaui-mcp.exe");
        w.Uninstall();

        Assert.All(cwds, c => Assert.Null(c));
    }

    // A hung `claude` must not be reported as a successful registration.
    [Fact]
    public void Install_does_not_report_Created_when_the_runner_times_out()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(ProcessRunner.TimedOut, ""));
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.NotEqual(AgentChange.Created, r.Change);
    }
}
```

- [ ] **Step 6: Widen the writer**

In `src/FlaUI.Mcp.Server/Install/ClaudeCodeConfigWriter.cs` replace lines 13-16:

```csharp
    private readonly Func<string, string[], string?, RunResult> _run;

    public ClaudeCodeConfigWriter(Func<string, string[], string?, RunResult>? runner = null)
        => _run = runner ?? DefaultRunner;
```

Replace the three call sites so each passes `null` for the working directory, and reads `.Code`:

```csharp
        _run("claude", new[] { "mcp", "remove", "--scope", "user", McpServerEntry.ServerName }, null);
```

```csharp
        var code = _run("claude", addArgs.ToArray(), null).Code;
```

```csharp
        var code = _run("claude", new[] { "mcp", "remove", "--scope", "user", McpServerEntry.ServerName }, null).Code;
```

Replace the whole `DefaultRunner` method (`:54-66`) with a delegation:

```csharp
    private static RunResult DefaultRunner(string file, string[] args, string? workingDirectory) =>
        ProcessRunner.Run(file, args, workingDirectory, ProcessRunner.DefaultTimeout);
```

Update the class doc (`:7-10`) — the "returns only an exit code" claim is now false, and it is quoted by the spec as the reason detection was thought infeasible:

```csharp
/// <summary>
/// Configures Claude Code via its stable `claude mcp add/remove` CLI. The runner returns the process
/// exit code plus stdout, and takes a working directory (null = inherit) because `claude plugin`
/// scopes bind to the CWD — see ClaudeCollisionRemedy. Injected for testability.
/// </summary>
```

Update the merge-overload doc at `:36-41`, whose stated premise ("the injected runner returns only an exit code (no stdout), so there is no way to read back") no longer holds:

```csharp
    /// <summary>Non-destructive-merge SHAPE, but NOT non-destructive BEHAVIOR: unlike AgyConfigWriter/
    /// GenericMcpConfigWriter, this writer has no locally-readable config — the registered args live inside
    /// the opaque `claude` CLI. The runner CAN now capture stdout, but `claude mcp` exposes no read-back of a
    /// registered server's args, so there is still nothing to merge against. `removeArgs` is therefore unused;
    /// `addArgs` is passed through to the existing full-replace `Install(exePath, args)` verbatim, which
    /// matches this writer's PRE-EXISTING behavior for every verb (no regression — it never merged).</summary>
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet build -c Debug` — expected: **0 Warning(s), 0 Error(s)**
Run: `dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"` — expected: **Failed: 0**, total ≥ 513.

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ProcessRunner.cs src/FlaUI.Mcp.Server/Install/ClaudeCodeConfigWriter.cs test/FlaUI.Mcp.Tests/Install/ProcessRunnerTests.cs test/FlaUI.Mcp.Tests/Install/ClaudeCodeConfigWriterTests.cs
git commit -m "feat(install): bounded process runner; widen the claude runner for stdout + cwd"
```

---

### Task 2: MEASURE `claude plugin disable` interactivity (M1)

**Why:** Task 1 bounded the hang, but a `disable` that *prompts* would now silently time out and report failure on every machine — the remedy would never work and we would not know why. The spec forbids assuming this. **This task writes no product code.**

**Files:** none (a measurement + a recorded result)

- [ ] **Step 1: Measure, without mutating real state**

Run against a throwaway profile so nothing real is touched:

```bash
export CLAUDE_CONFIG_DIR="$(mktemp -d)"
claude plugin disable --help
claude plugin disable no-such-plugin@no-such-marketplace --scope user < /dev/null ; echo "exit=$?"
claude plugin enable  no-such-plugin@no-such-marketplace --scope user < /dev/null ; echo "exit=$?"
```

Expected: both return promptly with a non-zero exit and a "not found"-style message. `< /dev/null` is the test — a command that prompts will either fail immediately on EOF or hang.

- [ ] **Step 2: Record the result in the spec**

Append the measured outcome under the spec's `⚠ BLOCKING HAZARD` section, replacing "**UNVERIFIED**" for M1 with the observed behavior and date.

- [ ] **Step 3: Decide**

- **Prompt-free (expected):** proceed to Task 3.
- **It prompts, or hangs on EOF:** **STOP and report.** The remedy needs a non-interactive channel; that is a design fork the user owns, not something to work around.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-07-16-skill-distribution-design.md
git commit -m "docs(spec): record the measured interactivity of claude plugin disable (M1)"
```

---

### Task 3: Parse `claude plugin list --json`

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ClaudePluginInventory.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudePluginInventoryTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/ClaudePluginInventoryTests.cs`:

```csharp
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudePluginInventoryTests
{
    // Shape copied from live output (2026-07-16). user-scope rows carry no projectPath.
    private const string Live = """
    [
      { "id": "agy-autotrain@clavity-agy-autotrain", "version": "0.1.5", "scope": "user", "enabled": true,
        "installPath": "C:\\Users\\u\\.claude\\plugins\\cache\\x", "installedAt": "2026-07-13T15:26:49.371Z" },
      { "id": "csharp-lsp@claude-plugins-official", "version": "1.0.0", "scope": "local", "enabled": true,
        "projectPath": "C:\\Users\\u\\Development\\Rust\\clavity", "installPath": "C:\\c" },
      { "id": "andrej-karpathy-skills@karpathy-skills", "version": "1.0.0", "scope": "user", "enabled": false,
        "installPath": "C:\\k" }
    ]
    """;

    [Fact]
    public void Parses_id_scope_enabled_and_projectPath()
    {
        var e = ClaudePluginInventory.Parse(Live);

        Assert.Equal(3, e.Count);
        Assert.Equal("agy-autotrain@clavity-agy-autotrain", e[0].Id);
        Assert.Equal("user", e[0].Scope);
        Assert.True(e[0].Enabled);
        Assert.Null(e[0].ProjectPath);                                        // user scope has none
        Assert.Equal("local", e[1].Scope);
        Assert.Equal(@"C:\Users\u\Development\Rust\clavity", e[1].ProjectPath);
        Assert.False(e[2].Enabled);
    }

    // The real driver for the whole multi-entry contract: one id, many rows, each its own project.
    [Fact]
    public void Keeps_every_row_of_a_repeated_id_with_its_own_scope_and_project()
    {
        const string json = """
        [
          { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": true,  "projectPath": "C:\\a" },
          { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\b" },
          { "id": "flaui-mcp@flaui-mcp", "scope": "user",  "enabled": true }
        ]
        """;

        var e = ClaudePluginInventory.Parse(json);

        Assert.Equal(3, e.Count);
        Assert.Equal(new[] { @"C:\a", @"C:\b", null }, e.Select(x => x.ProjectPath).ToArray());
        Assert.Equal(new[] { true, false, true }, e.Select(x => x.Enabled).ToArray());
    }

    [Fact]
    public void An_empty_array_yields_no_entries()
        => Assert.Empty(ClaudePluginInventory.Parse("[]"));

    // Every degenerate input must yield "I know nothing", never an exception: this runs inside a
    // hidden installer, where a throw becomes a mystery failure.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"not\": \"an array\" }")]
    [InlineData("[ 1, 2, 3 ]")]
    [InlineData("null")]
    [InlineData("[ { } ]")]
    [InlineData("[ { \"scope\": \"user\", \"enabled\": true } ]")]
    public void Degenerate_input_yields_no_entries_and_never_throws(string json)
        => Assert.Empty(ClaudePluginInventory.Parse(json));

    // An installed row with no explicit `enabled` is treated as ENABLED — the conservative reading:
    // it makes us look at it rather than silently skip a plugin that may be live.
    [Fact]
    public void A_row_without_enabled_is_treated_as_enabled()
    {
        var e = ClaudePluginInventory.Parse("""[ { "id": "x@y", "scope": "user" } ]""");
        Assert.True(Assert.Single(e).Enabled);
    }

    [Fact]
    public void A_row_without_scope_is_skipped_because_we_could_not_act_on_it()
        => Assert.Empty(ClaudePluginInventory.Parse("""[ { "id": "x@y", "enabled": true } ]"""));

    [Fact]
    public void Finds_all_rows_of_one_id()
    {
        var e = ClaudePluginInventory.Parse(Live);
        Assert.Single(ClaudePluginInventory.Matching(e, "csharp-lsp@claude-plugins-official"));
        Assert.Empty(ClaudePluginInventory.Matching(e, "flaui-mcp@flaui-mcp"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ClaudePluginInventoryTests"`
Expected: FAIL — `ClaudePluginInventory` not found.

- [ ] **Step 3: Write minimal implementation**

Create `src/FlaUI.Mcp.Server/Install/ClaudePluginInventory.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>One row of `claude plugin list --json`.</summary>
/// <param name="ProjectPath">Where the entry lives, for non-user scopes. NOT decoration: `claude
/// plugin disable` has no flag to target another project, so this is the working directory the
/// disable must RUN from, or it silently disables nothing.</param>
public sealed record ClaudePluginEntry(string Id, string Scope, bool Enabled, string? ProjectPath);

/// <summary>
/// Reads Claude Code's plugin inventory from `claude plugin list --json` — a documented
/// machine-readable contract, which is why we ask the host for its effective state instead of
/// re-implementing its scope resolution over its config files.
///
/// MEASURED 2026-07-16: the ROW SET is global — the same ids/scopes/projectPaths from any working
/// directory, backed by `~/.claude/plugins/installed_plugins.json`. But the `enabled` field of a
/// `scope=local` row is resolved against the CURRENT working directory, NOT the row's own projectPath:
/// run from a project whose settings enable the plugin and the row reads enabled; run from anywhere
/// else (including a hidden installer's cwd) and the SAME row reads disabled. This parser faithfully
/// transcribes whatever `claude` emitted — so a caller must treat `Enabled` on a non-user row as valid
/// ONLY when the list was read with the working directory set to that row's projectPath. See
/// ClaudeCollisionRemedy, which never trusts a global `Enabled` and uses `disable` itself as detector.
///
/// Never throws. It parses foreign output inside a hidden installer; "I know nothing" is a usable
/// answer, an exception is not.
/// </summary>
public static class ClaudePluginInventory
{
    public static IReadOnlyList<ClaudePluginEntry> Parse(string json)
    {
        var list = new List<ClaudePluginEntry>();
        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr) return list;
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var id = (string?)o["id"];
                var scope = (string?)o["scope"];
                // No id or no scope => nothing we could act on; drop it rather than guess.
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(scope)) continue;
                var enabled = o["enabled"] is null || ((bool?)o["enabled"] ?? true);
                list.Add(new ClaudePluginEntry(id, scope, enabled, (string?)o["projectPath"]));
            }
        }
        catch { return new List<ClaudePluginEntry>(); }
        return list;
    }

    public static IReadOnlyList<ClaudePluginEntry> Matching(IReadOnlyList<ClaudePluginEntry> entries, string id) =>
        entries.Where(e => string.Equals(e.Id, id, System.StringComparison.OrdinalIgnoreCase)).ToList();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ClaudePluginInventoryTests"`
Expected: PASS — 14 passed (7 of them the `[Theory]` rows).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudePluginInventory.cs test/FlaUI.Mcp.Tests/Install/ClaudePluginInventoryTests.cs
git commit -m "feat(install): parse claude plugin list --json (id/scope/enabled/projectPath)"
```

---

### Task 4: Deploy the Claude skill payload

**Why:** `ClaudeCodeConfigWriter` has **no file-writing code at all** (verified: it only shells `claude mcp`), and `AgyConfigWriter.cs:45-48` writes an **agy-shaped** bare `{name, version, description}` at the plugin root — not the `.claude-plugin/plugin.json` Claude needs. This is *not* free parity; it is a new responsibility with no home. What **is** free: `FlaUI.Mcp.Server.csproj:8-10` already embeds the SKILL.md as `FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md`, and a driving-only payload needs no new resource.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeSkillDeployerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/ClaudeSkillDeployerTests.cs`:

```csharp
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeSkillDeployerTests
{
    private static string TempConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-claude-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Deploy_writes_the_manifest_and_the_skill()
    {
        var cfg = TempConfigDir();

        var warning = new ClaudeSkillDeployer(cfg).Deploy();

        Assert.Null(warning);
        var root = Path.Combine(cfg, "skills", "flaui-mcp");
        Assert.True(File.Exists(Path.Combine(root, ".claude-plugin", "plugin.json")), "manifest deployed");
        Assert.True(File.Exists(Path.Combine(root, "skills", "driving-flaui-mcp", "SKILL.md")), "skill deployed");
    }

    // The manifest goes in .claude-plugin/ — an agy-shaped bare plugin.json at the root (what
    // AgyConfigWriter writes) is NOT a Claude Code plugin manifest.
    [Fact]
    public void The_manifest_is_claude_shaped_and_carries_the_assembly_version()
    {
        var cfg = TempConfigDir();
        new ClaudeSkillDeployer(cfg).Deploy();

        var json = File.ReadAllText(Path.Combine(cfg, "skills", "flaui-mcp", ".claude-plugin", "plugin.json"));

        Assert.Contains("\"name\": \"flaui-mcp\"", json);
        var av = typeof(ClaudeSkillDeployer).Assembly.GetName().Version!;
        Assert.Contains($"\"version\": \"{av.Major}.{av.Minor}.{av.Build}\"", json);   // 3-part semver, not 4
        Assert.False(File.Exists(Path.Combine(cfg, "skills", "flaui-mcp", "plugin.json")), "no agy-shaped root manifest");
    }

    [Fact]
    public void The_deployed_skill_is_the_embedded_seed()
    {
        var cfg = TempConfigDir();
        new ClaudeSkillDeployer(cfg).Deploy();

        var skill = File.ReadAllText(Path.Combine(cfg, "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md"));
        Assert.Contains("Driving FlaUI.Mcp", skill);
    }

    // Versioned product, not user state: a re-install must overwrite, or a user upgrading keeps a
    // drifted skill describing tools their new binary no longer has.
    [Fact]
    public void Deploy_is_idempotent_and_overwrites_a_stale_skill()
    {
        var cfg = TempConfigDir();
        var d = new ClaudeSkillDeployer(cfg);
        d.Deploy();
        var skill = Path.Combine(cfg, "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md");
        File.WriteAllText(skill, "STALE v0.14 CONTENT");

        var warning = d.Deploy();

        Assert.Null(warning);
        Assert.DoesNotContain("STALE", File.ReadAllText(skill));
    }

    // Same policy as the agy path: the skill rides along with the registration and must never deny
    // the user a working server. It reports, it does not throw.
    [Fact]
    public void Deploy_failure_returns_a_warning_and_never_throws()
    {
        var cfg = TempConfigDir();
        Directory.CreateDirectory(Path.Combine(cfg, "skills"));
        File.WriteAllText(Path.Combine(cfg, "skills", "flaui-mcp"), "blocker");   // a file where the dir must go

        var warning = new ClaudeSkillDeployer(cfg).Deploy();

        Assert.NotNull(warning);
        Assert.Contains("driving skill not deployed", warning);
    }

    [Fact]
    public void Remove_deletes_the_skill_tree()
    {
        var cfg = TempConfigDir();
        var d = new ClaudeSkillDeployer(cfg);
        d.Deploy();

        var warning = d.Remove();

        Assert.Null(warning);
        Assert.False(Directory.Exists(Path.Combine(cfg, "skills", "flaui-mcp")));
    }

    [Fact]
    public void Remove_on_a_machine_that_never_had_it_is_a_silent_no_op()
        => Assert.Null(new ClaudeSkillDeployer(TempConfigDir()).Remove());

    [Fact]
    public void Remove_survives_an_undeletable_tree_and_says_so()
    {
        var cfg = TempConfigDir();
        var d = new ClaudeSkillDeployer(cfg);
        d.Deploy();
        var held = Path.Combine(cfg, "skills", "flaui-mcp", "held-open.txt");

        using (File.Create(held))
        {
            var warning = d.Remove();
            Assert.NotNull(warning);
            Assert.Contains("left behind", warning);
        }
    }

    // Nothing outside our own namespace may be touched.
    [Fact]
    public void Remove_leaves_other_skills_alone()
    {
        var cfg = TempConfigDir();
        var other = Path.Combine(cfg, "skills", "someone-elses-skill");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "SKILL.md"), "not ours");
        var d = new ClaudeSkillDeployer(cfg);
        d.Deploy();

        d.Remove();

        Assert.True(File.Exists(Path.Combine(other, "SKILL.md")), "an unrelated skill was destroyed");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ClaudeSkillDeployerTests"`
Expected: FAIL — `ClaudeSkillDeployer` not found.

- [ ] **Step 3: Write minimal implementation**

Create `src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs`:

```csharp
namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Writes the driving skill into Claude Code's config dir as a `skills-dir` plugin, which Claude
/// auto-loads as `flaui-mcp@skills-dir` at user scope.
///
/// This is NOT the agy deployer with a different path: AgyConfigWriter writes an agy-shaped bare
/// plugin.json at the plugin root, whereas Claude Code needs `.claude-plugin/plugin.json`. The
/// EMBEDDED RESOURCE is shared (a driving-only payload needs no second copy); the manifest is not.
///
/// The manifest is GENERATED rather than embedded because its version must track the assembly at
/// runtime — mirroring what AgyConfigWriter already does for the peer.
///
/// Never throws: the skill rides along with the registration, and a failure to place it must not
/// deny the user a working server.
/// </summary>
public sealed class ClaudeSkillDeployer
{
    private const string PluginName = "flaui-mcp";
    private const string SkillResource = "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md";
    private readonly string _claudeConfigDir;

    public ClaudeSkillDeployer(string claudeConfigDir) => _claudeConfigDir = claudeConfigDir;

    /// <summary>`<claude-config>/skills/flaui-mcp` — the plugin root we own end to end.</summary>
    public string SkillRoot => Path.Combine(_claudeConfigDir, "skills", PluginName);

    /// <summary>Returns null on success, else the reason (reported as a Warning, never thrown).</summary>
    public string? Deploy()
    {
        try
        {
            var skillDir = Path.Combine(SkillRoot, "skills", "driving-flaui-mcp");
            Directory.CreateDirectory(skillDir);
            Directory.CreateDirectory(Path.Combine(SkillRoot, ".claude-plugin"));

            var av = typeof(ClaudeSkillDeployer).Assembly.GetName().Version;   // 4-part; trim to 3-part semver
            var version = av is null ? "0.0.0" : $"{av.Major}.{av.Minor}.{av.Build}";
            var pluginJson =
                "{\n  \"name\": \"flaui-mcp\",\n" +
                "  \"displayName\": \"FlaUI.Mcp\",\n" +
                "  \"version\": \"" + version + "\",\n" +
                "  \"description\": \"Driving skill for the flaui-mcp desktop-automation MCP server.\"\n}\n";
            // Measured: a working manifest needs no `skills` array — they auto-discover from skills/.
            File.WriteAllText(Path.Combine(SkillRoot, ".claude-plugin", "plugin.json"), pluginJson);

            using var res = typeof(ClaudeSkillDeployer).Assembly.GetManifestResourceStream(SkillResource)
                ?? throw new InvalidOperationException($"embedded seed skill '{SkillResource}' missing");
            using var outFile = File.Create(Path.Combine(skillDir, "SKILL.md"));
            res.CopyTo(outFile);
            return null;
        }
        catch (Exception e)
        {
            return $"driving skill not deployed to {SkillRoot}: {e.Message}";
        }
    }

    /// <summary>
    /// Remove the deployed skill. The skill is a PRODUCT ARTIFACT, not user data — a version-locked
    /// manual for tools that are going away — so it goes on a plain uninstall, and must: leaving it
    /// would tell the agent to call desktop_* tools that no longer exist.
    /// Recursive is safe: SkillRoot is our own namespace and holds only what Deploy() wrote.
    /// </summary>
    public string? Remove()
    {
        try
        {
            if (Directory.Exists(SkillRoot)) Directory.Delete(SkillRoot, recursive: true);
            return null;
        }
        catch (Exception e)
        {
            return $"driving skill left behind at {SkillRoot}: {e.Message}";
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ClaudeSkillDeployerTests"`
Expected: PASS — 9 passed.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs test/FlaUI.Mcp.Tests/Install/ClaudeSkillDeployerTests.cs
git commit -m "feat(install): deploy the driving skill to Claude Code's skills dir"
```

---

### Task 5: The collision marker

**Why:** Uninstall must re-enable what install disabled — **but only what install disabled**. Never re-enable a plugin the user disabled themselves. The marker is the only thing that can tell those apart, and the spec found four ways to get it wrong (R1, R3, R4, R7).

**Per-entry write-once (a refinement this plan makes explicit).** R1 says the marker is write-once so an idempotent re-install cannot overwrite it. Taken as a *file*-level rule that is wrong: if the user later installs the marketplace copy in a *second* project and we disable that new entry, a file-level "already exists, skip" would never record it, and it would never be restored. **The rule is per entry:** an entry is recorded by the run that transitioned *that entry*, and existing entries are never rewritten.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs`:

```csharp
using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class CollisionMarkerTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DisabledEntry UserEntry = new("flaui-mcp@flaui-mcp", "user", null);
    private static readonly DisabledEntry ProjA = new("flaui-mcp@flaui-mcp", "local", @"C:\a");
    private static readonly DisabledEntry ProjB = new("flaui-mcp@flaui-mcp", "local", @"C:\b");

    [Fact]
    public void Read_on_a_machine_with_no_marker_yields_nothing()
        => Assert.Empty(CollisionMarker.Read(TempState()));

    [Fact]
    public void Recorded_entries_round_trip_with_scope_and_project()
    {
        var s = TempState();

        CollisionMarker.Record(s, new[] { UserEntry, ProjA });

        var read = CollisionMarker.Read(s);
        Assert.Equal(2, read.Count);
        Assert.Contains(UserEntry, read);
        Assert.Contains(ProjA, read);
        Assert.Null(read.Single(e => e.Scope == "user").ProjectPath);
    }

    // R1, per entry: a repair/upgrade re-run must not disturb what an earlier run recorded.
    [Fact]
    public void Recording_an_entry_that_is_already_recorded_changes_nothing()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA });
        var before = File.ReadAllText(CollisionMarker.PathIn(s));

        CollisionMarker.Record(s, new[] { ProjA });

        Assert.Equal(before, File.ReadAllText(CollisionMarker.PathIn(s)));
        Assert.Single(CollisionMarker.Read(s));
    }

    // The case a FILE-level write-once rule would silently lose: a new entry disabled by a later run.
    [Fact]
    public void Recording_a_new_entry_merges_it_and_keeps_the_existing_ones()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA });

        CollisionMarker.Record(s, new[] { ProjB });

        var read = CollisionMarker.Read(s);
        Assert.Equal(2, read.Count);
        Assert.Contains(ProjA, read);
        Assert.Contains(ProjB, read);
    }

    // Same id and scope, different project => genuinely different entries.
    [Fact]
    public void Entries_are_distinguished_by_project_not_just_by_id_and_scope()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA, ProjB });
        Assert.Equal(2, CollisionMarker.Read(s).Count);
    }

    // R7: deleting the marker is part of consuming it. A marker that outlives its uninstall would
    // later re-enable a plugin the USER disabled — the exact outcome R1 exists to prevent.
    [Fact]
    public void Delete_removes_the_marker()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { UserEntry });

        var warning = CollisionMarker.Delete(s);

        Assert.Null(warning);
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void Delete_with_no_marker_present_is_a_silent_no_op()
        => Assert.Null(CollisionMarker.Delete(TempState()));

    [Fact]
    public void Delete_reports_a_warning_when_the_marker_cannot_be_removed()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { UserEntry });

        using (File.Open(CollisionMarker.PathIn(s), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var warning = CollisionMarker.Delete(s);
            Assert.NotNull(warning);
            Assert.Contains("marker", warning);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{ \"version\": 1 }")]
    [InlineData("{ \"version\": 1, \"disabled\": \"not an array\" }")]
    public void A_corrupt_marker_reads_as_empty_and_never_throws(string content)
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), content);
        Assert.Empty(CollisionMarker.Read(s));
    }

    // Fail-safe direction: an unreadable marker must mean "restore nothing", never "restore
    // everything" — we must not enable plugins we have no record of disabling.
    [Fact]
    public void Record_creates_the_state_dir_if_it_does_not_exist()
    {
        var s = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());   // NOT created

        CollisionMarker.Record(s, new[] { UserEntry });

        Assert.Single(CollisionMarker.Read(s));
    }

    [Fact]
    public void Recording_an_empty_set_writes_no_marker_at_all()
    {
        var s = TempState();
        CollisionMarker.Record(s, System.Array.Empty<DisabledEntry>());
        Assert.False(File.Exists(CollisionMarker.PathIn(s)), "an empty marker would later fire a no-op restore");
    }

    // ProjectPath is a WINDOWS path: C:\Proj and c:\proj are one directory. Default record equality
    // is case-sensitive, so without SameEntry the same entry would be recorded twice — and, worse,
    // the "did we already record this?" check would MISS, making a re-install wrongly conclude the
    // user disabled it.
    [Fact]
    public void An_entry_differing_only_in_path_casing_is_the_same_entry()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Proj") });

        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"c:\proj") });

        Assert.Single(CollisionMarker.Read(s));
    }

    [Fact]
    public void Entry_identity_ignores_case_in_id_scope_and_path()
    {
        Assert.True(CollisionMarker.SameEntry(
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Proj"),
            new DisabledEntry("FlaUI-MCP@FlaUI-MCP", "LOCAL", @"c:\proj")));
        Assert.False(CollisionMarker.SameEntry(
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\a"),
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\b")));
        Assert.False(CollisionMarker.SameEntry(
            new DisabledEntry("flaui-mcp@flaui-mcp", "user", null),
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", null)));
    }

    // We have already disabled the user's plugin by the time we record it. If the record does not
    // survive, uninstall can never put it back — so this failure must be VISIBLE, not swallowed.
    [Fact]
    public void A_marker_that_cannot_be_written_returns_a_warning_rather_than_failing_silently()
    {
        var s = TempState();
        Directory.CreateDirectory(CollisionMarker.PathIn(s));   // a DIRECTORY where the file must go

        var warning = CollisionMarker.Record(s, new[] { UserEntry });

        Assert.NotNull(warning);
        Assert.Contains("will not re-enable it automatically", warning);
    }

    [Fact]
    public void A_successful_record_returns_no_warning()
        => Assert.Null(CollisionMarker.Record(TempState(), new[] { UserEntry }));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~CollisionMarkerTests"`
Expected: FAIL — `CollisionMarker` not found.

- [ ] **Step 3: Write minimal implementation**

Create `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>One plugin entry that WE disabled, and everything needed to put it back.</summary>
/// <param name="ProjectPath">Null for user scope. For any other scope this is the directory the
/// enable must RUN from — `claude plugin enable` cannot target another project by flag.</param>
public sealed record DisabledEntry(string Id, string Scope, string? ProjectPath);

/// <summary>
/// The durable record of which plugin entries THIS product disabled, so uninstall can put back
/// exactly those and nothing else. Without it we cannot tell "we disabled it" from "the user
/// disabled it", and re-enabling the latter would override a deliberate user choice.
///
/// WHERE IT LIVES, and why not the obvious places:
///   - not `{app}`: Inno deletes it on BOTH uninstall branches (installer/flaui-mcp.iss:25).
///   - not `<data-dir>`: `--purge-data` deletes it (CliRouter.cs:110-114), and `--purge-data` is NOT
///     agent-scoped (CliRouter.cs:18) — so `uninstall --agent agy --purge-data` would destroy the
///     CLAUDE marker while Claude is still installed, stranding the user's plugin disabled forever.
///   - so: `<state-dir>`, outside both blast radii.
///
/// WRITE-ONCE, PER ENTRY (spec R1): only the run that performs a given entry's enabled->disabled
/// transition records THAT entry; later runs leave existing entries untouched. Per-entry rather than
/// per-file, because a later run may legitimately disable a NEW entry (e.g. the user installed the
/// marketplace copy in a second project) which must also be restored.
///
/// Never throws.
/// </summary>
public static class CollisionMarker
{
    public const string FileName = "disabled-plugins.json";

    public static string PathIn(string stateDir) => Path.Combine(stateDir, FileName);

    /// <summary>Two entries are the same entry when they name the same plugin in the same place.
    /// Paths and scopes are compared case-INSENSITIVELY: ProjectPath is a Windows path, so `C:\Proj`
    /// and `c:\proj` are one directory, and default record equality (ordinal, case-sensitive) would
    /// treat them as two. Getting this wrong duplicates entries in the marker and — worse — makes the
    /// "did we already record this?" check miss, so a re-install would wrongly conclude the USER
    /// disabled it. Restore's presence check uses the same comparison, deliberately.</summary>
    public static bool SameEntry(DisabledEntry a, DisabledEntry b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Scope, b.Scope, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ProjectPath ?? "", b.ProjectPath ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Merge these entries in, preserving any already recorded. Recording an entry that is
    /// already present is a no-op — the FIRST run to transition it owns the record.
    /// Returns null on success, else the reason: a marker we cannot write means uninstall will never
    /// restore the user's plugin, so the caller MUST be able to see that this failed.</summary>
    public static string? Record(string stateDir, IReadOnlyList<DisabledEntry> entries)
    {
        try
        {
            if (entries.Count == 0) return null;   // never write an empty marker: it would fire a no-op restore
            var merged = Read(stateDir).ToList();
            foreach (var e in entries)
                if (!merged.Any(m => SameEntry(m, e))) merged.Add(e);

            var arr = new JsonArray();
            foreach (var e in merged)
                arr.Add(new JsonObject
                {
                    ["id"] = e.Id,
                    ["scope"] = e.Scope,
                    ["projectPath"] = e.ProjectPath,
                });
            var root = new JsonObject { ["version"] = 1, ["disabled"] = arr };

            Directory.CreateDirectory(stateDir);
            File.WriteAllText(PathIn(stateDir), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception e)
        {
            // NOT swallowed. We have already disabled the user's plugin; if the record of that does
            // not survive, uninstall can never put it back and they lose it permanently. The caller
            // turns this into a Warning the user can actually see.
            return $"disabled a conflicting plugin but could NOT record it at {PathIn(stateDir)} ({e.Message}) — " +
                   "uninstalling flaui-mcp will not re-enable it automatically.";
        }
    }

    /// <summary>Entries we recorded. Empty when absent OR unreadable — the fail-safe direction: an
    /// unreadable marker must mean "restore nothing", never "enable things we have no record of".</summary>
    public static IReadOnlyList<DisabledEntry> Read(string stateDir)
    {
        var list = new List<DisabledEntry>();
        try
        {
            var path = PathIn(stateDir);
            if (!File.Exists(path)) return list;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return list;
            if (o["disabled"] is not JsonArray arr) return list;
            foreach (var node in arr)
            {
                if (node is not JsonObject e) continue;
                var id = (string?)e["id"];
                var scope = (string?)e["scope"];
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(scope)) continue;
                list.Add(new DisabledEntry(id, scope, (string?)e["projectPath"]));
            }
        }
        catch { return new List<DisabledEntry>(); }
        return list;
    }

    /// <summary>Consume the marker (spec R7). Returns null on success, else the reason.</summary>
    public static string? Delete(string stateDir)
    {
        try
        {
            var path = PathIn(stateDir);
            if (File.Exists(path)) File.Delete(path);
            return null;
        }
        catch (Exception e)
        {
            return $"could not remove the plugin-restore marker at {PathIn(stateDir)}: {e.Message}";
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~CollisionMarkerTests"`
Expected: PASS — 19 passed (5 of them `[Theory]` rows).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CollisionMarker.cs test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs
git commit -m "feat(install): per-entry write-once collision marker outside the purge path"
```

---

### Task 6: Detect and disable the colliding marketplace copy

**Why:** Anyone who followed `README.md:158-163` has `flaui-mcp@flaui-mcp` installed. Upgrading gives them the bundled copy **as well** — two plugins both shipping `driving-flaui-mcp`. **MEASURED: the collision is SILENT** — both report loaded, `claude plugin details` shows each owning the skill, and nothing anywhere reports a conflict. So "do nothing" is out: the user runs a drifted v0.14.0 skill with no indication at all. **Disable, not uninstall** — reversible, and it is what `README.md:306-311` already prescribes for this exact collision.

**Detection is by MUTATION, not by reading `enabled` (amended after Task 2, [Settled decisions](../specs/2026-07-16-skill-distribution-design.md) #4).** MEASURED 2026-07-16: `claude plugin list --json` resolves the `enabled` field of a `scope=local` row against the **current working directory**, not the row's own projectPath. A single global read from Setup's cwd reports every local copy as disabled — so a design that branched on `enabled` would silently skip a live local collision. Instead we enumerate rows by id from the global list (id/scope/projectPath are stable), then **attempt `disable` at each row's own scope+cwd** and read the outcome; `enabled` is re-read only at a row's own projectPath, and only to disambiguate a failed disable from an already-disabled one. This is the `mutation-as-detector` design both models converged on; per-project reads and message-string parsing were both rejected (see the ledger).

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCollisionRemedyTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A faithful model of the `claude plugin` CLI as MEASURED 2026-07-16. The remedy's correctness
    // rests on these EXACT semantics, so the fake reproduces them rather than letting a test hand-pick
    // exit codes it wishes were true:
    //   - `list --json` from cwd C: every installed row appears (id/scope/projectPath are global), but
    //     a LOCAL row's `enabled` is resolved against C — it reads its true state ONLY when C is its own
    //     projectPath, and reads false from anywhere else (THE bug the remedy must survive). A user
    //     row's `enabled` is global.
    //   - `disable id --scope s` from cwd: turns the target off and exits 0 if it was on; exits 1
    //     ("already disabled") and writes nothing if it was already off.
    //   - `enable id --scope s` from cwd: turns it on and exits 0 even for an id that does not exist
    //     (measured: enable does not validate), which is why restore must check presence first.
    private sealed class FakeClaude
    {
        // True per-target state. resolutionDir is "" for user scope, else the projectPath.
        private readonly Dictionary<string, bool> _state = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string id, string scope, string? pp)> _rows = new();
        public readonly List<(string[] args, string? cwd)> Calls = new();
        public bool ListFails;
        public string? RawListOverride;
        // cwd (or "" for null) -> forced disable exit code, WITHOUT mutating state. Models a genuine
        // failure (permissions, timeout) as distinct from the benign already-disabled exit 1.
        public readonly Dictionary<string, int> ForceDisableCode = new();
        // cwds at which `list --json` fails — used to model a re-read that cannot complete, so the
        // "disable failed AND state unverifiable" branch is reachable. Only the re-read passes a cwd.
        public readonly HashSet<string> FailListAtCwd = new();

        private static string ResDir(string scope, string? pp) =>
            scope.Equals("user", StringComparison.OrdinalIgnoreCase) ? "" : (pp ?? "");
        private static string Key(string id, string scope, string resDir) =>
            $"{id}|{scope}|{resDir}".ToLowerInvariant();

        public FakeClaude Install(string id, string scope, string? pp, bool enabled)
        {
            _rows.Add((id, scope, pp));
            _state[Key(id, scope, ResDir(scope, pp))] = enabled;
            return this;
        }

        public RunResult Run(string file, string[] args, string? cwd)
        {
            Calls.Add((args, cwd));
            if (args.Length >= 2 && args[0] == "plugin" && args[1] == "list")
            {
                if (ListFails) return new RunResult(ProcessRunner.NotFound, "");
                if (cwd is not null && FailListAtCwd.Contains(cwd)) return new RunResult(ProcessRunner.NotFound, "");
                if (RawListOverride is not null) return new RunResult(0, RawListOverride);
                return new RunResult(0, ListJsonFrom(cwd));
            }
            if (args.Length >= 5 && args[0] == "plugin" && args[1] == "disable")
            {
                if (ForceDisableCode.TryGetValue(cwd ?? "", out var forced)) return new RunResult(forced, "");
                var key = Key(args[2], args[4], ResDir(args[4], cwd));
                if (_state.TryGetValue(key, out var on) && on) { _state[key] = false; return new RunResult(0, ""); }
                return new RunResult(1, $"Plugin \"{args[2]}\" is already disabled");   // message NOT parsed by the remedy
            }
            if (args.Length >= 5 && args[0] == "plugin" && args[1] == "enable")
            {
                _state[Key(args[2], args[4], ResDir(args[4], cwd))] = true;    // succeeds even for a fictitious id
                return new RunResult(0, "");
            }
            return new RunResult(0, "");
        }

        // `enabled` AS `list --json` would report it from `cwd`: user rows read their global state; a
        // local row reads its true state only when listed from its own project, else false.
        private string ListJsonFrom(string? cwd)
        {
            var items = _rows.Select(r =>
            {
                bool enabled = r.scope.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? _state.GetValueOrDefault(Key(r.id, r.scope, ""))
                    : string.Equals(cwd ?? "", r.pp ?? "", StringComparison.OrdinalIgnoreCase)
                        && _state.GetValueOrDefault(Key(r.id, r.scope, r.pp ?? ""));
                var pp = r.pp is null ? "" : $", \"projectPath\": {JsonStr(r.pp)}";
                return $"{{ \"id\": {JsonStr(r.id)}, \"scope\": {JsonStr(r.scope)}, \"enabled\": {(enabled ? "true" : "false")}{pp} }}";
            });
            return "[" + string.Join(",", items) + "]";
        }

        private static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public IEnumerable<(string[] args, string? cwd)> Disables =>
            Calls.Where(c => c.args.Length >= 2 && c.args[0] == "plugin" && c.args[1] == "disable");
    }

    // dirExists defaults to always-true: the fakes use synthetic projectPaths that do not exist on the
    // test machine, and only the deleted-project test cares about the guard.
    private static ClaudeCollisionRemedy Remedy(FakeClaude cli, string state, Func<string, bool>? dirExists = null)
        => new(cli.Run, state, dirExists ?? (_ => true));

    // Guard the guard: prove the fake actually reproduces the measured CWD-resolution bug, so the
    // regression test below is not vacuously green against a fake that lists the true state everywhere.
    [Fact]
    public void The_fake_reproduces_the_cwd_resolution_bug()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\Proj", enabled: true);
        Assert.Contains("\"enabled\": false", cli.Run("claude", new[] { "plugin", "list", "--json" }, null).Output);         // global: WRONG
        Assert.Contains("\"enabled\": true", cli.Run("claude", new[] { "plugin", "list", "--json" }, @"C:\Proj").Output);    // own cwd: right
    }

    [Fact]
    public void No_marketplace_copy_means_no_disable_and_no_marker()
    {
        var cli = new FakeClaude().Install("other@thing", "user", null, true);
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.Null(warning);
        Assert.Empty(cli.Disables);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void An_enabled_user_scope_copy_is_disabled_at_user_scope_with_no_cwd()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = Assert.Single(cli.Disables);
        Assert.Equal(new[] { "plugin", "disable", "flaui-mcp@flaui-mcp", "--scope", "user" }, d.args);
        Assert.Null(d.cwd);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "user", null), Assert.Single(CollisionMarker.Read(s)));
    }

    // THE REGRESSION THAT MOTIVATES THE WHOLE MECHANISM. A local copy that is genuinely ENABLED at its
    // own project reads `enabled: false` from the global list (Setup's cwd has no .claude). A design
    // that trusted the global `enabled` would skip it and leave the collision live. Mutation-as-detector
    // disables it anyway, because it acts at the project's own cwd. This test FAILS against the old
    // read-based Apply and PASSES against the mutation-based one.
    [Fact]
    public void A_local_copy_the_global_list_wrongly_reports_disabled_is_still_disabled()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\Proj", enabled: true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = Assert.Single(cli.Disables);
        Assert.Equal(@"C:\Proj", d.cwd);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Proj"), Assert.Single(CollisionMarker.Read(s)));
    }

    // THE CWD-BINDING CONTRACT. `claude plugin disable` has no flag to target another project, and
    // --scope local writes the CURRENT directory's .claude/settings.local.json. Fired from Setup's
    // cwd it would litter a stray settings file and disable nothing.
    [Fact]
    public void A_local_scope_copy_is_disabled_from_its_own_project_path()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\Projects\MyCode", enabled: true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = Assert.Single(cli.Disables);
        Assert.Equal(new[] { "plugin", "disable", "flaui-mcp@flaui-mcp", "--scope", "local" }, d.args);
        Assert.Equal(@"C:\Projects\MyCode", d.cwd);
    }

    [Fact]
    public void Every_entry_is_disabled_at_its_own_scope_and_its_own_project()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\a", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "project", @"C:\b", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = cli.Disables.ToList();
        Assert.Equal(3, d.Count);
        Assert.Equal(new[] { @"C:\a", @"C:\b", null }, d.Select(x => x.cwd).ToArray());
        Assert.Equal(new[] { "local", "project", "user" }, d.Select(x => x.args[4]).ToArray());
        Assert.Equal(3, CollisionMarker.Read(s).Count);
    }

    // R1 + R5. Already disabled with no marker => the USER did this. Never re-record it, and SAY so —
    // the correct rule here is invisible, and an unreported decision is indistinguishable from a bug.
    // Note: mutation-as-detector still ATTEMPTS the disable (a no-op exit 1), then re-reads to confirm
    // it is off; the invariant is "nothing recorded + a warning", not "no disable attempted".
    [Fact]
    public void An_already_disabled_copy_with_no_marker_is_left_alone_and_reported()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: false);
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.Empty(CollisionMarker.Read(s));
        Assert.NotNull(warning);
        Assert.Contains("already disabled", warning);
    }

    // The R1 hazard itself: a repair or minor-version re-install must not rewrite the marker. The
    // entry is already off and we already have its record, so the re-read confirms it and we stay silent.
    [Fact]
    public void A_reinstall_over_our_own_disable_leaves_the_marker_untouched()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: false);
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var before = File.ReadAllText(CollisionMarker.PathIn(s));

        var warning = Remedy(cli, s).Apply();

        Assert.Null(warning);
        Assert.Equal(before, File.ReadAllText(CollisionMarker.PathIn(s)));
    }

    [Fact]
    public void A_newly_enabled_entry_is_merged_into_an_existing_marker()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\a", enabled: false)
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\b", enabled: true);
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\a") });

        Remedy(cli, s).Apply();

        Assert.Equal(2, CollisionMarker.Read(s).Count);   // C:\a preserved, C:\b merged in
    }

    // We only record what we actually transitioned. A GENUINE disable failure (exit 1 with the entry
    // still enabled) must be reported and NOT recorded — recording it would make uninstall "restore" a
    // plugin that was never disabled by us. Distinguished from already-disabled by the re-read.
    [Fact]
    public void A_genuinely_failed_disable_is_reported_and_NOT_recorded()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        cli.ForceDisableCode[""] = 1;   // user-scope cwd is "" — fail the disable without turning it off
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("could not disable", warning);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void A_timed_out_disable_is_reported_and_NOT_recorded()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        cli.ForceDisableCode[""] = ProcessRunner.TimedOut;
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("timed out", warning);
        Assert.Empty(CollisionMarker.Read(s));
    }

    // The defensive third branch of the exit-1 handler: disable genuinely fails (exit 1, entry still
    // enabled) AND the re-read cannot complete. We must warn and record NOTHING — recording an
    // unconfirmed transition would make uninstall "restore" (enable) a plugin we never disabled.
    [Fact]
    public void A_failed_disable_whose_state_cannot_be_reread_is_reported_and_NOT_recorded()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\p", enabled: true);
        cli.ForceDisableCode[@"C:\p"] = 1;    // genuine failure, entry stays enabled
        cli.FailListAtCwd.Add(@"C:\p");        // ...and the re-read at its own cwd cannot complete
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("could not verify", warning);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void One_failed_disable_does_not_stop_the_others()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\a", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        cli.ForceDisableCode[@"C:\a"] = 1;   // the local one fails; the user one still succeeds
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.Equal(2, cli.Disables.Count());                             // both attempted
        Assert.NotNull(warning);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "user", null), Assert.Single(CollisionMarker.Read(s)));
    }

    // A non-user entry whose project directory is gone cannot load the plugin, and disable would have
    // nowhere valid to run from. Skip it — do not attempt the disable, do not record it.
    [Fact]
    public void A_row_whose_project_directory_is_gone_is_skipped()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\gone", enabled: true);
        var s = TempState();

        var warning = Remedy(cli, s, dirExists: p => p != @"C:\gone").Apply();

        Assert.Empty(cli.Disables);
        Assert.Empty(CollisionMarker.Read(s));
        Assert.NotNull(warning);
        Assert.Contains("no longer exists", warning);
    }

    [Fact]
    public void A_missing_claude_cli_is_reported_and_disables_nothing()
    {
        var cli = new FakeClaude { ListFails = true };
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Empty(cli.Disables);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void Unparseable_list_output_is_reported_and_disables_nothing()
    {
        var cli = new FakeClaude { RawListOverride = "<html>not json</html>" };
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Empty(cli.Disables);
    }

    // agy panel round 2: a non-empty array that parses to ZERO of our rows (e.g. a future claude
    // release renames the `id` field) must surface as a warning, NOT be read as "no collisions" and
    // silently skipped — that would leave a live collision entirely unreported.
    [Fact]
    public void A_list_that_parses_to_no_rows_but_is_not_an_empty_array_is_reported_not_skipped()
    {
        var cli = new FakeClaude { RawListOverride = """[ { "pluginId": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": true } ]""" };
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Empty(cli.Disables);
    }

    // agy panel round 2: when the marker cannot be written, the summary must NOT promise a restore it
    // cannot deliver — the record-failure warning already says the opposite, and joining both is
    // self-contradictory to the one human who reads it.
    [Fact]
    public void When_the_marker_cannot_be_written_the_summary_does_not_promise_a_restore()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        var s = TempState();
        Directory.CreateDirectory(CollisionMarker.PathIn(s));   // a DIRECTORY where the marker file must go

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("will not re-enable it automatically", warning);              // the honest part
        Assert.DoesNotContain("they will be re-enabled if you uninstall", warning);   // the promise is withheld
    }

    [Fact]
    public void The_inventory_is_enumerated_with_json_and_no_cwd()
    {
        var cli = new FakeClaude();
        Remedy(cli, TempState()).Apply();

        var list = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "plugin", "list", "--json" }, list.args);
        Assert.Null(list.cwd);          // enumeration is global; only remediation is CWD-bound
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ClaudeCollisionRemedyTests"`
Expected: FAIL — `ClaudeCollisionRemedy` not found.

- [ ] **Step 3: Write minimal implementation**

Create `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Neutralises the v0.14.x marketplace copy of our own plugin, reversibly.
///
/// WHY THIS EXISTS: anyone who followed the README installed `flaui-mcp@flaui-mcp` from the
/// marketplace. Once the installer also bundles `flaui-mcp@skills-dir`, both ship a skill named
/// `driving-flaui-mcp`. MEASURED 2026-07-16: the collision is SILENT — both plugins report loaded and
/// nothing warns — so the user unknowingly runs a drifted v0.14.0 skill against a new binary. Doing
/// nothing is not an option; neither is warning only (Setup is runhidden, so a warning reaches nobody).
///
/// DISABLE, NOT UNINSTALL: both stop the drifted skill loading, but disable is reversible, and
/// uninstall would silently destroy something the user installed deliberately because our own README
/// told them to.
///
/// DETECTION IS BY MUTATION, NOT BY READING `enabled`. MEASURED 2026-07-16 (plan Task 2): `claude
/// plugin list --json` resolves the `enabled` field of a `scope=local` row against the CURRENT working
/// directory, not the row's own projectPath. A single global read from Setup's cwd (which has no
/// `.claude`) reports every local copy as disabled — so branching on `enabled` would silently skip a
/// live local collision, the exact failure this class exists to prevent. Instead we ATTEMPT the
/// disable at each row's own scope+cwd and read the OUTCOME: `disable` reports exit 0 when it actually
/// turned an enabled entry off, and exit 1 ("already disabled") when it was already off. The global
/// list is used only to ENUMERATE rows by id (id/scope/projectPath ARE global and stable); `enabled`
/// is re-read only at a row's own projectPath, and only to disambiguate a failed disable.
/// </summary>
public sealed class ClaudeCollisionRemedy
{
    public const string MarketplaceId = "flaui-mcp@flaui-mcp";

    private readonly Func<string, string[], string?, RunResult> _run;
    private readonly string _stateDir;
    private readonly Func<string, bool> _dirExists;

    /// <param name="dirExists">Injected for testability; defaults to <see cref="Directory.Exists"/>.
    /// A non-user entry whose project directory is gone cannot load the plugin, and `disable` would
    /// have nowhere valid to run from — such entries are skipped.</param>
    public ClaudeCollisionRemedy(Func<string, string[], string?, RunResult> run, string stateDir,
        Func<string, bool>? dirExists = null)
    {
        _run = run;
        _stateDir = stateDir;
        _dirExists = dirExists ?? Directory.Exists;
    }

    /// <summary>Install side: disable each enabled colliding entry and record it. Returns a warning
    /// summary, or null when there was nothing to say.</summary>
    public string? Apply()
    {
        var warnings = new List<string>();

        if (!TryReadInventory(out var entries, out var listWarning))
            return listWarning;

        var matching = ClaudePluginInventory.Matching(entries, MarketplaceId);
        if (matching.Count == 0) return null;

        var recorded = CollisionMarker.Read(_stateDir);
        var justDisabled = new List<DisabledEntry>();

        foreach (var e in matching)
        {
            var entry = new DisabledEntry(e.Id, e.Scope, e.ProjectPath);

            // A non-user entry whose project directory is gone cannot load the plugin (no collision),
            // and `disable` would have nowhere valid to run from. Skip it; a stale marker record for
            // it is handled at restore, not here.
            if (e.ProjectPath is not null && !_dirExists(e.ProjectPath))
            {
                warnings.Add($"a conflicting {e.Id} was listed for {e.ProjectPath}, which no longer exists — skipped it.");
                continue;
            }

            // MUTATION-AS-DETECTOR. We do NOT branch on e.Enabled — it is CWD-resolved and unreliable
            // for local rows (see class summary). `disable` at the row's own cwd IS the detector, and
            // the scope is not a free parameter: `claude plugin disable` cannot target another project,
            // so it must RUN from that entry's projectPath (null = user scope = anywhere).
            var r = _run("claude", new[] { "plugin", "disable", e.Id, "--scope", e.Scope }, e.ProjectPath);
            if (r.Code == 0)
            {
                justDisabled.Add(entry);   // it was enabled; WE transitioned it enabled -> disabled
                continue;
            }
            if (r.Code == ProcessRunner.NotFound || r.Code == ProcessRunner.TimedOut)
            {
                warnings.Add($"could not disable the conflicting {e.Id} ({Where(entry)}): claude " +
                             (r.Code == ProcessRunner.TimedOut ? "timed out." : "was not found.") +
                             " Two copies of the driving skill may be active.");
                continue;
            }

            // Non-zero, non-sentinel: `disable` refuses an already-disabled entry with exit 1, which
            // is ambiguous with a genuine failure (permissions, corrupt settings). The distinguishing
            // text is a human-readable message we must NOT parse — re-read the entry's ACTUAL state
            // from its OWN project directory, the one context where `enabled` is reliable for it.
            var stillEnabled = ReadEnabledAt(entry);
            if (stillEnabled == true)
            {
                warnings.Add($"could not disable the conflicting {e.Id} ({Where(entry)}): claude exited {r.Code}. " +
                             "Two copies of the driving skill may now be active.");
            }
            else if (stillEnabled == false)
            {
                // Already disabled. A record of our own means this is a reinstall over our prior
                // disable — stay silent (R1). No record means the USER disabled it: leave it, and SAY
                // so (R5), because this correct decision is otherwise invisible. Case-insensitive, so a
                // path differing only in casing is the SAME entry and we do not blame the user for our
                // own disable.
                if (!recorded.Any(m => CollisionMarker.SameEntry(m, entry)))
                    warnings.Add($"{e.Id} ({Where(entry)}) was already disabled and we have no record of disabling it — " +
                                 "assuming you did, and leaving it alone.");
            }
            else
            {
                // disable failed AND the state could not be re-read. Report conservatively; record
                // nothing, because no transition was ever confirmed.
                warnings.Add($"could not disable the conflicting {e.Id} ({Where(entry)}) and could not verify its state. " +
                             "Two copies of the driving skill may be active.");
            }
        }

        // Only entries WE transitioned are recorded; existing records are never rewritten (R1).
        // A marker we cannot write is NOT a silent shrug: we have already disabled the user's plugin,
        // so a lost record means uninstall never puts it back.
        var recordWarning = CollisionMarker.Record(_stateDir, justDisabled);
        if (recordWarning is not null) warnings.Add(recordWarning);

        if (justDisabled.Count > 0)
            // Only PROMISE a restore if we actually recorded it. If Record failed, recordWarning
            // already tells the user it will NOT be re-enabled — promising the opposite in the same
            // concatenated line is worse than saying nothing. (agy panel round 2.)
            warnings.Insert(0, recordWarning is null
                ? $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill " +
                  "(they will be re-enabled if you uninstall flaui-mcp)."
                : $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill.");

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    /// <summary>Uninstall side: put back exactly what we disabled, then consume the marker (R7).</summary>
    public string? Restore()
    {
        var recorded = CollisionMarker.Read(_stateDir);
        if (recorded.Count == 0) return null;

        var warnings = new List<string>();

        // If we cannot read the inventory at all (e.g. the claude CLI is gone), we cannot restore —
        // and we must NOT consume the marker. It is still an ACCURATE record of a plugin we disabled
        // and have not put back; deleting it here would strand the user's plugin disabled with no
        // record anywhere that we were the ones who did it. R7's stale-marker hazard is about a
        // marker surviving a SUCCESSFUL consume, which this is not.
        if (!TryReadInventory(out var entries, out var listWarning))
        {
            // Manual recourse, but only for entries we could actually act on: an entry whose project
            // directory is gone is moot AND a `cd` into a deleted path is impossible, so listing it
            // would resurrect the same bad-recourse defect the in-loop guard below fixes. (agy panel
            // round 2 — the early return bypassed that guard.)
            var recoverable = recorded.Where(e => e.ProjectPath is null || _dirExists(e.ProjectPath)).ToList();
            var recourse = recoverable.Count == 0
                ? "No manual action is possible (the recorded projects no longer exist)."
                : "To restore manually: " + string.Join("; ", recoverable.Select(e =>
                    $"claude plugin enable {e.Id} --scope {e.Scope}" +
                    (e.ProjectPath is null ? "" : $" (run from {e.ProjectPath})")));
            return $"{listWarning} Your conflicting plugin(s) are still disabled and were NOT re-enabled. " +
                   "The record is kept at " + CollisionMarker.PathIn(_stateDir) + ". " + recourse;
        }

        var present = ClaudePluginInventory.Matching(entries, MarketplaceId);

        foreach (var e in recorded)
        {
            // Symmetric to Apply's guard: a project deleted AFTER we disabled the copy but BEFORE
            // uninstall cannot load the plugin (the collision is moot) and `enable` has nowhere valid
            // to run from. MEASURED: Process.Start with a missing working directory throws
            // Win32Exception, which ProcessRunner surfaces as a failed run — so without this guard we
            // would fall through to the failure branch below and print an impossible "run it from
            // <deleted path>" recourse. The `present` check does NOT catch this: the inventory can
            // still LIST a stale row for a deleted project (measured).
            if (e.ProjectPath is not null && !_dirExists(e.ProjectPath))
            {
                warnings.Add($"{e.Id} ({Where(e)}) — its project directory no longer exists, so there was nothing to re-enable.");
                continue;
            }

            // R2: the user may have uninstalled it themselves after we disabled it. Enabling a
            // plugin that no longer exists writes a phantom {id:true} (measured: enable succeeds for a
            // nonexistent id) — check the id is still installed first.
            if (!present.Any(p => Same(p, e)))
            {
                warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, so it was not re-enabled.");
                continue;
            }

            var r = _run("claude", new[] { "plugin", "enable", e.Id, "--scope", e.Scope }, e.ProjectPath);
            if (r.Code != 0)
                warnings.Add($"could not re-enable {e.Id} ({Where(e)}): claude exited {r.Code}. " +
                             $"To restore it yourself: claude plugin enable {e.Id} --scope {e.Scope}" +
                             (e.ProjectPath is null ? "" : $" (run it from {e.ProjectPath})"));
        }

        // R7: deleting the marker is part of consuming it. Delete even when a restore failed — a
        // surviving marker would later re-enable a plugin the user had deliberately disabled, which
        // is the exact outcome R1 exists to prevent. The failure is reported instead.
        var deleteWarning = CollisionMarker.Delete(_stateDir);
        if (deleteWarning is not null) warnings.Add(deleteWarning);

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    /// <summary>Re-read one entry's ACTUAL enabled state from its OWN project directory — the only
    /// context in which `list --json`'s CWD-resolved `enabled` is correct for a local-scope row. Null
    /// when it cannot be read back. Used ONLY to disambiguate a failed disable, never as the primary
    /// detector.</summary>
    private bool? ReadEnabledAt(DisabledEntry e)
    {
        var r = _run("claude", new[] { "plugin", "list", "--json" }, e.ProjectPath);
        if (r.Code != 0) return null;
        var row = ClaudePluginInventory.Parse(r.Output)
            .FirstOrDefault(p => CollisionMarker.SameEntry(new DisabledEntry(p.Id, p.Scope, p.ProjectPath), e));
        return row?.Enabled;
    }

    private bool TryReadInventory(out IReadOnlyList<ClaudePluginEntry> entries, out string? warning)
    {
        var r = _run("claude", new[] { "plugin", "list", "--json" }, null);   // enumeration is global
        if (r.Code != 0)
        {
            entries = System.Array.Empty<ClaudePluginEntry>();
            // Translate the sentinels rather than leaking "-1"/"-2" (internal, not OS exit codes) to a
            // human. (agy panel round 2.)
            warning = r.Code switch
            {
                ProcessRunner.NotFound => "claude CLI not on PATH — did not check for a conflicting marketplace plugin.",
                ProcessRunner.TimedOut => "`claude plugin list --json` timed out — did not check for a conflicting marketplace plugin.",
                _ => $"`claude plugin list --json` exited {r.Code} — did not check for a conflicting marketplace plugin.",
            };
            return false;
        }
        entries = ClaudePluginInventory.Parse(r.Output);
        if (entries.Count == 0 && !LooksLikeEmptyList(r.Output))
        {
            warning = "could not read `claude plugin list --json` output — did not check for a conflicting marketplace plugin.";
            return false;
        }
        warning = null;
        return true;
    }

    // Genuinely empty ONLY when the output parses to an empty JSON array. A non-empty array that
    // yielded zero entries means the schema drifted (e.g. a renamed `id` field) — that must surface as
    // a warning, NOT be read as "no collisions" and silently skipped. A bare StartsWith("[") could not
    // tell those apart. Whitespace variants like "[ ]" parse to Count 0, so they are handled too.
    // (agy panel round 2 — mechanism corrected: extra fields do NOT break Parse; a renamed field does.)
    private static bool LooksLikeEmptyList(string output)
    {
        try { return JsonNode.Parse(output) is JsonArray { Count: 0 }; }
        catch { return false; }
    }

    private static bool Same(ClaudePluginEntry p, DisabledEntry e) =>
        CollisionMarker.SameEntry(new DisabledEntry(p.Id, p.Scope, p.ProjectPath), e);

    private static string Where(DisabledEntry e) => e.ProjectPath is null ? $"scope {e.Scope}" : $"scope {e.Scope} in {e.ProjectPath}";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ClaudeCollisionRemedyTests"`
Expected: PASS — 19 passed.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs
git commit -m "feat(install): detect and disable the colliding marketplace copy, at its own scope and cwd"
```

---

### Task 7: Symmetric restore

**Why:** If uninstall removed the bundled skill but left the marketplace copy disabled, the user would end up with **no driving skill at all** and no idea why — we disabled their working plugin and then took away its replacement. `Restore()` was written in Task 6; this task tests it exhaustively.

**Files:**
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCollisionRestoreTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class FakeCli
    {
        public string ListJson = "[]";
        public int EnableCode;
        public readonly List<(string[] args, string? cwd)> Calls = new();

        public RunResult Run(string file, string[] args, string? cwd)
        {
            if (args.Length >= 2 && args[0] == "plugin" && args[1] == "list")
                return new RunResult(0, ListJson);
            Calls.Add((args, cwd));
            return new RunResult(EnableCode, "");
        }
    }

    [Fact]
    public void With_no_marker_nothing_is_enabled()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""" };

        var warning = new ClaudeCollisionRemedy(cli.Run, TempState()).Restore();

        Assert.Null(warning);
        Assert.Empty(cli.Calls);   // never re-enable a plugin the USER disabled
    }

    [Fact]
    public void A_recorded_user_entry_is_re_enabled_and_the_marker_consumed()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Null(warning);
        var c = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "plugin", "enable", "flaui-mcp@flaui-mcp", "--scope", "user" }, c.args);
        Assert.Null(c.cwd);
        Assert.False(File.Exists(CollisionMarker.PathIn(s)), "R7: the marker must be consumed");
    }

    [Fact]
    public void A_recorded_local_entry_is_re_enabled_from_its_project_path()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\Projects\\MyCode" } ]"""
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Projects\MyCode") });

        new ClaudeCollisionRemedy(cli.Run, s).Restore();

        var c = Assert.Single(cli.Calls);
        Assert.Equal(@"C:\Projects\MyCode", c.cwd);
    }

    [Fact]
    public void Every_recorded_entry_is_restored_at_its_own_scope_and_project()
    {
        var cli = new FakeCli
        {
            ListJson = """
            [
              { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\a" },
              { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\b" },
              { "id": "flaui-mcp@flaui-mcp", "scope": "user",  "enabled": false }
            ]
            """
        };
        var s = TempState();
        CollisionMarker.Record(s, new[]
        {
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\a"),
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\b"),
            new DisabledEntry("flaui-mcp@flaui-mcp", "user", null),
        });

        new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Equal(new[] { @"C:\a", @"C:\b", null }, cli.Calls.Select(c => c.cwd).ToArray());
    }

    // R2: the user may have uninstalled the plugin after we disabled it. Enabling something that is
    // gone throws or orphans a reference in their settings.
    [Fact]
    public void An_entry_whose_plugin_is_gone_is_not_enabled_and_is_reported()
    {
        var cli = new FakeCli { ListJson = "[]" };                       // they removed it themselves
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Empty(cli.Calls);
        Assert.NotNull(warning);
        Assert.Contains("no longer installed", warning);
        Assert.False(File.Exists(CollisionMarker.PathIn(s)), "a moot marker must still be consumed");
    }

    // A failed restore must degrade to a warning and never throw: cleanup must not derail uninstall.
    [Fact]
    public void A_failed_enable_is_reported_with_a_manual_recourse_and_never_throws()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = 1,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("could not re-enable", warning);
        Assert.Contains("claude plugin enable flaui-mcp@flaui-mcp --scope user", warning);   // the user can fix it
    }

    // R7's failure mode, stated as a test: a marker that survives its uninstall would later
    // re-enable a plugin the user had deliberately disabled.
    [Fact]
    public void The_marker_is_consumed_even_when_a_restore_fails()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = 1,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
    }

    [Fact]
    public void Restoring_an_entry_the_user_already_re_enabled_is_harmless()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": true } ]""" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Null(warning);
        Assert.Single(cli.Calls);   // enable is idempotent; the marker is consumed either way
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
    }

    // If we cannot even see what is installed, we cannot restore — and we must KEEP the marker. It
    // is still an accurate record of a plugin we disabled and have not put back; consuming it here
    // would strand the user's plugin disabled with no record anywhere that we did it. (R7's
    // stale-marker hazard is about surviving a SUCCESSFUL consume — this is not one.)
    [Fact]
    public void A_missing_claude_cli_at_restore_time_KEEPS_the_marker_and_reports_a_manual_recourse()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\proj") });

        // dirExists: _ => true so the recorded project counts as present and its recourse line is emitted
        // (the round-2 fix omits deleted projects from the recourse; here the project still exists).
        var warning = new ClaudeCollisionRemedy((_, _, _) => new RunResult(ProcessRunner.NotFound, ""), s, _ => true).Restore();

        Assert.NotNull(warning);
        Assert.True(File.Exists(CollisionMarker.PathIn(s)), "consuming the marker here loses the record forever");
        Assert.Contains("still disabled", warning);
        Assert.Contains("claude plugin enable flaui-mcp@flaui-mcp --scope local", warning);
        Assert.Contains(@"C:\proj", warning);          // they need to know WHERE to run it
    }

    // agy panel round 2: the CLI-missing early return must ALSO skip deleted-project entries in its
    // manual-recourse text — otherwise it tells the user to `cd` into a directory that is gone.
    [Fact]
    public void The_manual_recourse_omits_entries_whose_project_directory_is_gone()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\gone") });

        var warning = new ClaudeCollisionRemedy((_, _, _) => new RunResult(ProcessRunner.NotFound, ""), s, p => p != @"C:\gone").Restore();

        Assert.NotNull(warning);
        Assert.DoesNotContain(@"C:\gone", warning);      // no impossible "run from C:\gone"
        Assert.Contains("no longer exist", warning);      // says why there is nothing to do
        Assert.True(File.Exists(CollisionMarker.PathIn(s)), "cannot verify => must KEEP the marker");
    }

    // Symmetric to Apply's deleted-project guard (agy panel, round 1). A project deleted after we
    // disabled the copy but before uninstall: we must NOT try to enable at a dead cwd (Process.Start
    // throws Win32Exception on a missing working directory, which surfaces as a failed run and would
    // otherwise print an impossible "run it from <deleted path>" recourse), must not crash, and must
    // still consume the marker. The stale row can still appear in `list --json`, so the presence
    // check alone does not cover this.
    [Fact]
    public void A_recorded_entry_whose_project_directory_is_gone_is_skipped_not_run()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\gone" } ]"""
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\gone") });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, p => p != @"C:\gone").Restore();

        Assert.Empty(cli.Calls);                                 // never spawned `enable` at a dead cwd
        Assert.NotNull(warning);
        Assert.Contains("no longer exists", warning);
        Assert.DoesNotContain("run it from", warning);           // no impossible recourse
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));    // R7: marker still consumed
    }

    // THE FULL ROUND TRIP — the property that actually matters to a user.
    [Fact]
    public void Install_then_uninstall_leaves_the_users_plugin_exactly_as_it_was_found()
    {
        var s = TempState();
        var enabled = true;
        var calls = new List<string[]>();
        RunResult Run(string f, string[] a, string? cwd)
        {
            if (a[0] == "plugin" && a[1] == "list")
                return new RunResult(0, $$"""[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": {{(enabled ? "true" : "false")}} } ]""");
            calls.Add(a);
            // Faithful: disable of an already-off entry is a no-op exit 1 (measured); of an on entry, exit 0.
            if (a[1] == "disable") { if (!enabled) return new RunResult(1, "already disabled"); enabled = false; return new RunResult(0, ""); }
            if (a[1] == "enable") { enabled = true; return new RunResult(0, ""); }
            return new RunResult(0, "");
        }

        new ClaudeCollisionRemedy(Run, s).Apply();
        Assert.False(enabled, "install must disable the colliding copy");

        new ClaudeCollisionRemedy(Run, s).Restore();

        Assert.True(enabled, "uninstall must put the user's plugin back");
        Assert.Equal(new[] { "disable", "enable" }, calls.Select(c => c[1]).ToArray());
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
    }

    // And the inverse property: what we never disabled, we never enable.
    [Fact]
    public void A_plugin_the_user_disabled_is_still_disabled_after_install_then_uninstall()
    {
        var s = TempState();
        var enabled = false;                       // the user disabled it themselves, before we ever ran
        var calls = new List<string[]>();
        RunResult Run(string f, string[] a, string? cwd)
        {
            if (a[0] == "plugin" && a[1] == "list")
                return new RunResult(0, $$"""[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": {{(enabled ? "true" : "false")}} } ]""");
            calls.Add(a);
            // Faithful: the user already disabled it, so our detector's disable is a no-op exit 1.
            if (a[1] == "disable") { if (!enabled) return new RunResult(1, "already disabled"); enabled = false; return new RunResult(0, ""); }
            if (a[1] == "enable") { enabled = true; return new RunResult(0, ""); }
            return new RunResult(0, "");
        }

        new ClaudeCollisionRemedy(Run, s).Apply();
        new ClaudeCollisionRemedy(Run, s).Restore();

        Assert.False(enabled, "we re-enabled a plugin the USER had disabled");
        Assert.DoesNotContain(calls, c => c[1] == "enable");   // the invariant: never re-enable what we did not disable
        Assert.Empty(CollisionMarker.Read(s));                 // and nothing was recorded to restore
    }
}
```

- [ ] **Step 2: Run test to verify it fails, then passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ClaudeCollisionRestoreTests"`
Expected: PASS — 13 passed. `Restore()` already exists from Task 6; if any test fails, fix `ClaudeCollisionRemedy.Restore`, **not** the test.

- [ ] **Step 3: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs
git commit -m "test(install): pin symmetric restore, including both round-trip properties"
```

---

### Task 8: Durable uninstall warnings

**Why:** Uninstall deletes the exe (`ops-manual.md:45-47`), so `flaui-mcp status` — the reader for all of this — is gone moments later, and `--purge-data` deletes `install.log` itself. Every warning the restore path emits is destroyed as it is written, exactly for the user who most needs it. This file is what the Inno uninstaller (Task 11) will surface.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/UninstallWarnings.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/UninstallWarningsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/UninstallWarningsTests.cs`:

```csharp
using System;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class UninstallWarningsTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Writes_the_warning_lines_where_the_uninstaller_can_find_them()
    {
        var s = TempState();

        var path = UninstallWarnings.Write(s, new[] { "could not re-enable x", "y is gone" });

        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("could not re-enable x", text);
        Assert.Contains("y is gone", text);
    }

    // An empty file would pop an EMPTY dialog at the end of a clean uninstall — worse than silence.
    [Fact]
    public void Writes_no_file_when_there_is_nothing_to_warn_about()
    {
        var s = TempState();

        var path = UninstallWarnings.Write(s, Array.Empty<string>());

        Assert.Null(path);
        Assert.False(File.Exists(UninstallWarnings.PathIn(s)));
    }

    // A clean uninstall after a dirty one must not resurrect the old warnings.
    [Fact]
    public void Writing_nothing_clears_a_stale_warnings_file()
    {
        var s = TempState();
        UninstallWarnings.Write(s, new[] { "old news" });

        UninstallWarnings.Write(s, Array.Empty<string>());

        Assert.False(File.Exists(UninstallWarnings.PathIn(s)), "a stale file would be shown as if it were current");
    }

    [Fact]
    public void A_second_write_replaces_rather_than_appends()
    {
        var s = TempState();
        UninstallWarnings.Write(s, new[] { "first" });

        UninstallWarnings.Write(s, new[] { "second" });

        var text = File.ReadAllText(UninstallWarnings.PathIn(s));
        Assert.DoesNotContain("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Creates_the_state_dir_when_absent()
    {
        var s = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());   // NOT created

        var path = UninstallWarnings.Write(s, new[] { "something" });

        Assert.NotNull(path);
        Assert.True(File.Exists(path!));
    }

    // The reporter must never itself become the failure.
    [Fact]
    public void An_unwritable_target_returns_null_and_never_throws()
    {
        var s = TempState();
        using (File.Open(UninstallWarnings.PathIn(s), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var path = UninstallWarnings.Write(s, new[] { "blocked" });
            Assert.Null(path);
        }
    }

    [Fact]
    public void The_file_lives_in_the_state_dir_not_the_data_dir()
    {
        // The data dir is what --purge-data destroys; these warnings must outlive that.
        Assert.EndsWith(Path.Combine("state-x", "uninstall-warnings.log"), UninstallWarnings.PathIn("state-x"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~UninstallWarningsTests"`
Expected: FAIL — `UninstallWarnings` not found.

- [ ] **Step 3: Write minimal implementation**

Create `src/FlaUI.Mcp.Server/Install/UninstallWarnings.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Warnings from an uninstall, parked where they outlive the uninstall.
///
/// WHY THIS IS NOT install.log: uninstall deletes the exe (docs/ops-manual.md:45-47), so
/// `flaui-mcp status` — the reader for install.log — no longer exists moments later; and
/// `--purge-data` deletes install.log itself. A warning written there during uninstall is destroyed
/// at the moment it is written, for exactly the user who needs it.
///
/// The Inno uninstaller reads this file after our CLI has run and shows it to the user
/// (installer/flaui-mcp.iss), because it is the only actor still standing once the exe is gone.
/// </summary>
public static class UninstallWarnings
{
    public const string FileName = "uninstall-warnings.log";

    public static string PathIn(string stateDir) => Path.Combine(stateDir, FileName);

    /// <summary>Best-effort. Returns the path written, or null if there was nothing to say (or we
    /// could not say it). An EMPTY set removes any stale file rather than leaving last time's
    /// warnings to be shown as if they were current.</summary>
    public static string? Write(string stateDir, IReadOnlyList<string> lines)
    {
        try
        {
            var path = PathIn(stateDir);
            if (lines.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return null;
            }
            Directory.CreateDirectory(stateDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            File.WriteAllLines(path, new[] { $"flaui-mcp uninstall — {stamp}", "" }.Concat(lines));
            return path;
        }
        catch { return null; }   // the reporter must never itself become the failure
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~UninstallWarningsTests"`
Expected: PASS — 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/UninstallWarnings.cs test/FlaUI.Mcp.Tests/Install/UninstallWarningsTests.cs
git commit -m "feat(install): durable uninstall warnings outside the purge blast radius"
```

---

### Task 9: Wire it into the router

**Why:** Everything above is inert until `CliRouter` resolves the new paths and calls it. Two ordering facts matter: `Apply` (which does the restore) already runs **before** `Report` (`CliRouter.cs:104`) and `--purge-data` (`:110-114`), so **restore → report → purge** holds today; and `--purge-data` is read **independently of `--agent`** (`:18`), which is exactly why the marker is not in the data dir.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs:203`, `:207-220`, `:222-246`, `:103-115`
- Test: `test/FlaUI.Mcp.Tests/Install/CliRouterClaudeSkillTests.cs`

- [ ] **Step 0: Verify state**

Confirm `CliRouter.cs:203` reads:

```csharp
    private readonly record struct Paths(string AgyServers, string AgyPerms, string GenericPath, string DataDir, string AgyPluginsDir);
```

and that `:236` reads `var w = new ClaudeCodeConfigWriter();`. If not, STOP: `STATE_MISMATCH`.

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/CliRouterClaudeSkillTests.cs`:

```csharp
using System;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

// These drive CliRouter.Run end to end, so they MUST redirect every path away from the real
// profile. `81dedd7` had to fix tests that wrote into the real ~/.flaui-mcp precisely because a
// path lacked an override.
//
// Env vars are process-global. Two things make this safe, and both are pre-existing repo facts —
// do not "improve" on them:
//   - xunit.runner.json sets parallelizeTestCollections:false, so test classes run sequentially.
//     No [Collection] attribute is needed; adding one would invent a convention this repo lacks.
//   - We SAVE AND RESTORE each variable rather than nulling it (the pattern at CliRouterTests.cs:55,71).
//     Nulling would clobber a real CLAUDE_CONFIG_DIR on the machine of anyone who actually sets it.
public class CliRouterClaudeSkillTests : IDisposable
{
    private static readonly string[] Vars =
        { "FLAUI_MCP_DATA_DIR", "FLAUI_MCP_STATE_DIR", "FLAUI_MCP_CLAUDE_CONFIG_DIR", "FLAUI_MCP_AGY_PLUGINS_DIR", "CLAUDE_CONFIG_DIR" };

    private readonly string _root;
    private readonly Dictionary<string, string?> _saved = new();

    public CliRouterClaudeSkillTests()
    {
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);

        _root = Path.Combine(Path.GetTempPath(), "flaui-router-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_STATE_DIR", Path.Combine(_root, "state"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR", Path.Combine(_root, "claude"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_AGY_PLUGINS_DIR", Path.Combine(_root, "agy"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);   // our override must be what wins
    }

    public void Dispose()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, _saved[v]);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ClaudeSkill => Path.Combine(_root, "claude", "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md");

    [Fact]
    public void Install_deploys_the_claude_skill()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        Assert.True(File.Exists(ClaudeSkill), "the driving skill was not deployed for Claude Code");
    }

    [Fact]
    public void Uninstall_removes_the_claude_skill()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);

        CliRouter.Run(new[] { "uninstall", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);

        Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills", "flaui-mcp")));
    }

    // The payload is unconditional in the sense of "no contributor gate" — it must NOT mean "write
    // regardless of host". --agent all is the installer's default (installer/flaui-mcp.iss:38), so an
    // unguarded write would create an orphaned ~/.claude/skills/flaui-mcp on EVERY agy-only machine.
    [Fact]
    public void Install_deploys_nothing_for_claude_when_the_claude_cli_is_absent()
    {
        var outp = new StringWriter();
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
            Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills", "flaui-mcp")),
                "deployed a skill for a client we could not register");
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    // The NotFound gate belongs to INSTALL only. Removing the skill dir is pure file I/O and needs
    // no CLI — gating it would strand a skill on disk telling the agent to call tools that are gone.
    [Fact]
    public void Uninstall_still_removes_the_skill_when_the_claude_cli_is_absent()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        Assert.True(File.Exists(ClaudeSkill));

        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
            Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills", "flaui-mcp")),
                "a skill for deleted tools was left on disk because the CLI happened to be off PATH");
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    // The permanent-loss path: `claude` off PATH for a minute must not cost the user their plugin.
    [Fact]
    public void Uninstall_with_the_cli_absent_keeps_the_restore_marker_and_warns()
    {
        var state = Path.Combine(_root, "state");
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var outp = new StringWriter();

        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);

            Assert.True(File.Exists(CollisionMarker.PathIn(state)), "the marker was consumed without restoring anything");
            var warnings = File.ReadAllText(UninstallWarnings.PathIn(state));
            Assert.Contains("still disabled", warnings);
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    // R6, end to end: an uninstall warning must reach the file the Inno uninstaller reads, and must
    // survive the purge that runs in the same breath.
    [Fact]
    public void Uninstall_warnings_are_parked_where_they_survive_the_purge()
    {
        var state = Path.Combine(_root, "state");
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var outp = new StringWriter();

        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "all", "--purge-data", "--config", Path.Combine(_root, "c.json") },
                @"C:\x\flaui-mcp.exe", outp);

            Assert.False(Directory.Exists(Path.Combine(_root, "data")), "the purge did not happen");
            Assert.True(File.Exists(UninstallWarnings.PathIn(state)), "the purge destroyed the warnings it was supposed to outlive");
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    [Fact]
    public void Agy_only_install_does_not_touch_the_claude_config_dir()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "agy", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills")));
    }

    // R4, as a router-level test: --purge-data is read independently of --agent (CliRouter.cs:18),
    // so an agy-targeted purge wipes the SHARED data dir. The Claude marker must survive it, or a
    // later `uninstall --agent claude` concludes "the user disabled it" and strands them forever.
    [Fact]
    public void An_agy_purge_does_not_destroy_the_claude_restore_marker()
    {
        var state = Path.Combine(_root, "state");
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var outp = new StringWriter();

        CliRouter.Run(new[] { "uninstall", "--agent", "agy", "--purge-data", "--config", Path.Combine(_root, "c.json") },
            @"C:\x\flaui-mcp.exe", outp);

        Assert.True(File.Exists(CollisionMarker.PathIn(state)), "an agy purge destroyed the Claude restore marker");
    }

    [Fact]
    public void Purge_still_removes_the_data_dir()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "generic", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        CliRouter.Run(new[] { "uninstall", "--agent", "all", "--purge-data", "--config", Path.Combine(_root, "c.json") },
            @"C:\x\flaui-mcp.exe", outp);

        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void CLAUDE_CONFIG_DIR_is_honored_when_our_own_override_is_absent()
    {
        Environment.SetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR", null);
        var claudeHome = Path.Combine(_root, "claude-home");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeHome);
        try
        {
            var outp = new StringWriter();
            CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
            Assert.True(File.Exists(Path.Combine(claudeHome, "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md")));
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~CliRouterClaudeSkillTests"`
Expected: FAIL — several tests fail; `FLAUI_MCP_STATE_DIR` and the Claude skill deploy do not exist yet.

- [ ] **Step 3: Extend the Paths record**

Replace `CliRouter.cs:203`:

```csharp
    private readonly record struct Paths(string AgyServers, string AgyPerms, string GenericPath, string DataDir, string AgyPluginsDir, string ClaudeConfigDir, string StateDir);
```

- [ ] **Step 4: Resolve the two new paths**

In `ResolvePaths`, immediately before the `return new Paths(...)` line, insert:

```csharp
        // Claude Code honors CLAUDE_CONFIG_DIR (measured: with it pointed at an empty dir,
        // `claude plugin list` reports "No plugins installed"). A hardcoded ~/.claude would write
        // where the host is not reading, for any user who sets it. FLAUI_MCP_* wins so tests never
        // touch the real profile.
        var claudeConfigDir = Environment.GetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR")
                              ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                              ?? Path.Combine(home, ".claude");

        // NOT under dataDir: --purge-data deletes that, and it is not agent-scoped (see :18), so an
        // `uninstall --agent agy --purge-data` would take the Claude restore marker with it.
        // NOT under {app}: Inno deletes that on both uninstall branches.
        var stateDir = Environment.GetEnvironmentVariable("FLAUI_MCP_STATE_DIR")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlaUI.Mcp", "state");
```

and extend the return:

```csharp
        return new Paths(agyServers, agyPerms, genericPath, dataDir, agyPlugins, claudeConfigDir, stateDir);
```

- [ ] **Step 5: Wire the claude branch of `Apply`**

Replace the claude branch of `Apply` (`CliRouter.cs:233-238`):

```csharp
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("claude", () =>
            {
                var runner = ClaudeRunner();
                var w = new ClaudeCodeConfigWriter(runner);
                var r = install ? w.Install(exePath, extraArgs) : w.Uninstall();
                var deployer = new ClaudeSkillDeployer(paths.ClaudeConfigDir);
                var remedy = new ClaudeCollisionRemedy(runner, paths.StateDir);

                string? skillWarning, collisionWarning;
                if (install)
                {
                    // Gate the DEPLOY — and only the deploy — on the client existing. Deploying a
                    // skill for a client we could not register is pointless by construction, and with
                    // --agent all as the installer's default, an unguarded write would leave an
                    // orphaned skills dir on every agy-only machine.
                    if (r.Change == AgentChange.NotFound) return r;
                    skillWarning = deployer.Deploy();
                    collisionWarning = remedy.Apply();
                }
                else
                {
                    // NO such gate on uninstall, deliberately. Removing the skill dir is pure file
                    // I/O and needs no CLI at all — gating it would strand the skill on disk, telling
                    // the agent to call desktop_* tools that no longer exist. And skipping Restore()
                    // would ORPHAN the marker and leave the user's plugin disabled forever: the exact
                    // permanent-loss outcome R1/R2 exist to prevent, triggered by nothing worse than
                    // `claude` being off PATH for a minute. Restore() handles a missing CLI itself —
                    // it reports and KEEPS the marker rather than consuming it.
                    skillWarning = deployer.Remove();
                    collisionWarning = remedy.Restore();
                }

                var combined = string.Join(" ", new[] { r.Warning, skillWarning, collisionWarning }.Where(x => x is not null));
                return r with
                {
                    Detail = install ? $"{r.Detail}; {deployer.SkillRoot}" : r.Detail,
                    Warning = combined.Length == 0 ? null : combined,
                };
            }));
```

Add the runner factory next to `Isolate`:

```csharp
    /// The claude runner, with a test seam for the "CLI absent" case — the branch that gates the
    /// skill deploy, and the one a machine without Claude Code actually takes.
    private static Func<string, string[], string?, RunResult> ClaudeRunner() =>
        Environment.GetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING") == "1"
            ? (_, _, _) => new RunResult(ProcessRunner.NotFound, "")
            : (file, args, cwd) => ProcessRunner.Run(file, args, cwd, ProcessRunner.DefaultTimeout);
```

Add `using System.Linq;` to the top of `CliRouter.cs` if it is not already present.

- [ ] **Step 6: Park the uninstall warnings before the purge**

In the `uninstall` case, replace `CliRouter.cs:104`:

```csharp
                var uninstallResults = Apply(agent, paths, install: false, exePath).ToList();
                Report(uninstallResults, "uninstall", paths.DataDir, outp);
                // The exe is about to be deleted and install.log may be purged, so a warning written
                // now is destroyed as it is written. Park it where the Inno uninstaller can find it.
                UninstallWarnings.Write(paths.StateDir,
                    uninstallResults.Where(r => r.Warning is not null).Select(r => $"[{r.Agent}] {r.Warning}").ToList());
```

The ordering that matters — **restore (inside `Apply`) → report → park → purge** — already holds: `Apply` precedes `Report`, and `purgeData` runs last at `:110-114`. Do not reorder those.

- [ ] **Step 7: Run the full suite**

Run: `dotnet build -c Debug` — expected **0 Warning(s), 0 Error(s)**
Run: `dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"` — expected **Failed: 0**

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/CliRouterClaudeSkillTests.cs
git commit -m "feat(install): wire the Claude skill deploy, collision remedy and warnings into the router"
```

---

### Task 10: Report it in `status`

**Why:** Setup runs `runhidden`, so nothing printed at install time reaches anyone. `flaui-mcp status` is the read side, and it currently reports only the agy seed.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/InstallStatus.cs:21-37`
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs:34-36`
- Test: `test/FlaUI.Mcp.Tests/Install/InstallStatusClaudeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/InstallStatusClaudeTests.cs`:

```csharp
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class InstallStatusClaudeTests
{
    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-status-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Reports_the_claude_skill_as_not_deployed_when_it_is_absent()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp());
        Assert.Contains("Driving skill (Claude Code):", text);
        Assert.Contains("NOT deployed", text);
    }

    [Fact]
    public void Reports_the_claude_skill_and_its_version_once_deployed()
    {
        var claude = Temp();
        new ClaudeSkillDeployer(claude).Deploy();

        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp());

        var av = typeof(InstallStatus).Assembly.GetName().Version!;
        Assert.Contains("deployed", text);
        Assert.Contains($"v{av.Major}.{av.Minor}.{av.Build}", text);
    }

    // R5's channel: "we disabled your marketplace copy" is otherwise invisible, and Setup ran hidden.
    [Fact]
    public void Reports_a_marketplace_copy_we_disabled()
    {
        var state = Temp();
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\proj") });

        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), state);

        Assert.Contains("flaui-mcp@flaui-mcp", text);
        Assert.Contains(@"C:\proj", text);
        Assert.Contains("re-enabled if you uninstall", text);
    }

    [Fact]
    public void Says_nothing_about_collisions_when_there_are_none()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp());
        Assert.DoesNotContain("flaui-mcp@flaui-mcp", text);
    }

    [Fact]
    public void Still_reports_the_agy_seed()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp());
        Assert.Contains("Seed driving skill (agy):", text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InstallStatusClaudeTests"`
Expected: FAIL — `Describe` takes 3 arguments, not 5.

- [ ] **Step 3: Extend `Describe`**

Replace `InstallStatus.cs:21-37`:

```csharp
    public static string Describe(string exePath, string agyPluginsDir, string dataDir, string claudeConfigDir, string stateDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"flaui-mcp {typeof(InstallStatus).Assembly.GetName().Version}");
        sb.AppendLine($"  exe: {exePath}");
        sb.AppendLine();

        var pluginRoot = Path.Combine(agyPluginsDir, "flaui-mcp");
        sb.AppendLine("Seed driving skill (agy):");
        sb.AppendLine("  " + DescribeSeed(pluginRoot));
        sb.AppendLine();

        sb.AppendLine("Driving skill (Claude Code):");
        sb.AppendLine("  " + DescribeClaudeSkill(new ClaudeSkillDeployer(claudeConfigDir).SkillRoot));
        sb.AppendLine();

        var collisions = DescribeCollisions(stateDir);
        if (collisions is not null) { sb.AppendLine(collisions); sb.AppendLine(); }

        var log = Path.Combine(dataDir, LogName);
        sb.AppendLine("Last install/uninstall run:");
        sb.Append(DescribeLog(log));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Same shape as the agy seed report, but a different tree and a different manifest
    /// location — Claude Code's manifest lives in `.claude-plugin/`.</summary>
    private static string DescribeClaudeSkill(string skillRoot)
    {
        var skill = Path.Combine(skillRoot, "skills", "driving-flaui-mcp", "SKILL.md");
        if (!File.Exists(skill))
            return $"NOT deployed — nothing at {skillRoot}. If you installed Claude Code after " +
                   "flaui-mcp, run: flaui-mcp install --agent claude";
        // ReadDeployedVersion ALREADY EXISTS in this class (InstallStatus.cs:51) and takes the
        // manifest path as a parameter, so it is location-agnostic — reuse it, do not write a second
        // JSON parser. (A panel seat called this an undefined symbol; it is defined at :51.)
        var deployed = ReadDeployedVersion(Path.Combine(skillRoot, ".claude-plugin", "plugin.json"));
        return $"deployed ({deployed}) at {skillRoot}";
    }

    /// <summary>The R5 channel: a plugin we disabled on the user's behalf is otherwise invisible —
    /// Setup ran hidden, so this is where they can find out.</summary>
    private static string? DescribeCollisions(string stateDir)
    {
        var recorded = CollisionMarker.Read(stateDir);
        if (recorded.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Conflicting plugins we disabled (they will be re-enabled if you uninstall flaui-mcp):");
        foreach (var e in recorded)
            sb.AppendLine($"  {e.Id} — scope {e.Scope}{(e.ProjectPath is null ? "" : $" in {e.ProjectPath}")}");
        return sb.ToString().TrimEnd();
    }
```

- [ ] **Step 4: Update the caller**

Replace `CliRouter.cs:35`:

```csharp
                outp.WriteLine(InstallStatus.Describe(exePath, paths.AgyPluginsDir, paths.DataDir, paths.ClaudeConfigDir, paths.StateDir));
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet build -c Debug` — expected **0 Warning(s)**
Run: `dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"` — expected **Failed: 0**

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/InstallStatus.cs src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/InstallStatusClaudeTests.cs
git commit -m "feat(install): report the Claude skill and any disabled marketplace copy in status"
```

---

### Task 11: The Inno uninstall dialog — and its silent-mode guard

**Why:** Task 8 parked the warnings; nothing shows them. The Inno uninstaller is the only actor that outlives the exe it deletes.

**⚠ The trap this task exists to avoid.** Under `/VERYSILENT`, Inno **suppresses** the MsgBox and auto-answers the default — `flaui-mcp.iss:83` **depends** on that behavior today ("Default button is No (MB_DEFBUTTON2) so a /VERYSILENT uninstall keeps the user's config"). A naive *show → delete* would therefore become **delete-without-showing**: the warning destroyed having reached nobody, and the user's plugin lost with no trace. **The reap is only legitimate if the dialog was actually displayed.**

**Files:**
- Modify: `installer/flaui-mcp.iss:113-117`

- [ ] **Step 1: MEASURE `UninstallSilent` (M2)**

`UninstallSilent` is **UNVERIFIED** — it is documented for Inno Setup 6, but this plan has not run it. Confirm before relying on it:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /Q installer\flaui-mcp.iss
```

If compilation fails with `Unknown identifier 'UninstallSilent'`, **STOP and report** — the guard needs a different mechanism (candidate: check `UninstallProgressForm.Visible`), and that is a fork, not a detail.

- [ ] **Step 2: Add the reporting procedure**

In `installer/flaui-mcp.iss`, insert immediately **before** `procedure CurUninstallStepChanged`:

```pascal
// The CLI wrote any restore warnings here and is about to be deleted along with `flaui-mcp status`,
// the only thing that could have read them. We are the last actor standing, so we report them.
function StateDir(): string;
begin
  Result := ExpandConstant('{localappdata}\FlaUI.Mcp\state');
end;

procedure ShowUninstallWarnings();
var
  LogPath: string;
  Contents: AnsiString;
begin
  LogPath := StateDir() + '\uninstall-warnings.log';
  if not FileExists(LogPath) then
    exit;

  // A silent uninstall suppresses MsgBox and auto-answers the default (this script already relies
  // on that at InitializeUninstall). Deleting the log here would therefore destroy the warning
  // having shown it to nobody. Keep the evidence instead: there is no human to honor a purge
  // prompt for either, because nobody answered one.
  if UninstallSilent then
    exit;

  // If we cannot READ it we must not DELETE it: reaping here would destroy the evidence having
  // shown it to nobody, which is the very failure this procedure exists to prevent. Leave it and
  // point at it instead — a file the user can still open beats a file we silently ate.
  if not LoadStringFromFile(LogPath, Contents) then
  begin
    MsgBox('FlaUI.Mcp was removed, but some cleanup did not complete and the details could not be read.'
           + #13#10#13#10 + 'They were left for you at:' + #13#10 + LogPath, mbInformation, MB_OK);
    exit;
  end;

  MsgBox('FlaUI.Mcp was removed, but some cleanup did not complete:' + #13#10#13#10 +
         String(Contents), mbInformation, MB_OK);

  // Reaped ONLY on the path where a human has actually seen the contents — this honors the
  // "remove my configuration" request without silently swallowing the reason they may need.
  DeleteFile(LogPath);
  RemoveDir(StateDir());
  RemoveDir(ExpandConstant('{localappdata}\FlaUI.Mcp'));
end;
```

- [ ] **Step 3: Call it from the existing hook**

Replace `installer/flaui-mcp.iss:113-117` with:

```pascal
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromUserPath(ExpandConstant('{app}'));
    ShowUninstallWarnings();
  end;
end;
```

- [ ] **Step 4: Compile**

Run: `& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /Q installer\flaui-mcp.iss`
Expected: exits 0, `dist\flaui-mcp-setup.exe` produced.

- [ ] **Step 5: Verify the guard by inspection**

`RemoveDir` only removes **empty** directories, so a `state` dir still holding the marker is left alone — correct, since an interrupted uninstall must not lose the marker.

Confirm by reading the file back: the `if UninstallSilent then exit;` line must sit **before** the first `DeleteFile`. If the reap can run without the dialog, the guard is not doing its job.

- [ ] **Step 6: Commit**

```bash
git add installer/flaui-mcp.iss
git commit -m "feat(installer): surface uninstall warnings at usPostUninstall, guarded against silent runs"
```

---

### Task 12: The smoke gate that would have caught v0.14.0

**Why:** `claude plugin validate` **passes** on a load-broken manifest — validation is not a load test, which is how v0.14.0 shipped a plugin that could not load at all. Two properties make this gate real:

1. **Run from OUTSIDE this repo.** Inside it, project-scope skills mask the result — that is exactly why the breakage looked fine to us.
2. **⚠ Assert a NEGATIVE.** `✔ loaded` alone is a **FALSE GREEN**: the collision is silent and **both** plugins report loaded, so a gate that only checks *our* plugin passes even when the disable step failed or never ran — green-lighting the poisoned two-skill runtime this whole remedy exists to prevent.

**⚠ CI cannot run this** — `ci.yml`/`release.yml` have no Claude Code CLI and no `~/.claude` (measured). It is a local/manual gate. Do not add it to a workflow.

**Files:**
- Create: `scripts/install-smoke.ps1`

- [ ] **Step 1: Write the script**

Create `scripts/install-smoke.ps1`:

```powershell
#requires -Version 7
<#
.SYNOPSIS
  Local/manual gate for skill distribution. NOT runnable in CI: no claude CLI, no ~/.claude there.
.DESCRIPTION
  Runs in a THROWAWAY CLAUDE_CONFIG_DIR so the real profile is never touched, and asserts both:
    - our bundled skill loads, and
    - a seeded colliding marketplace copy does NOT.
  The negative is the point: both plugins load silently, so asserting only our own would pass even
  when the disable never ran.
#>
[CmdletBinding()]
param([string]$Exe = "$PSScriptRoot\..\publish\flaui-mcp.exe")

$ErrorActionPreference = 'Stop'
$failed = @()
function Check($name, $cond) {
    if ($cond) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:failed += $name }
}

if (-not (Test-Path $Exe)) { throw "no exe at $Exe — run: dotnet publish src/FlaUI.Mcp.Server -c Release -o publish" }

$sandbox  = Join-Path ([IO.Path]::GetTempPath()) "flaui-smoke-$([guid]::NewGuid())"
$claude   = Join-Path $sandbox 'claude'
$state    = Join-Path $sandbox 'state'
$outside  = Join-Path $sandbox 'outside-the-repo'
New-Item -ItemType Directory -Force -Path $claude, $state, $outside | Out-Null

$env:CLAUDE_CONFIG_DIR          = $claude
$env:FLAUI_MCP_STATE_DIR        = $state
$env:FLAUI_MCP_DATA_DIR         = Join-Path $sandbox 'data'
$env:FLAUI_MCP_AGY_PLUGINS_DIR  = Join-Path $sandbox 'agy'
Push-Location $outside      # inside the repo, project-scope skills mask the result
try {
    Write-Host "`n== install ==" -ForegroundColor Cyan
    & $Exe install --agent claude | Out-Host

    $root = Join-Path $claude 'skills\flaui-mcp'
    Check 'manifest deployed'  (Test-Path (Join-Path $root '.claude-plugin\plugin.json'))
    Check 'skill deployed'     (Test-Path (Join-Path $root 'skills\driving-flaui-mcp\SKILL.md'))

    Write-Host "`n== version lockstep ==" -ForegroundColor Cyan
    $exeVer      = (& $Exe --version) -replace '^flaui-mcp\s+',''
    $manifestVer = (Get-Content (Join-Path $root '.claude-plugin\plugin.json') -Raw | ConvertFrom-Json).version
    Check "manifest $manifestVer matches exe $exeVer" ($exeVer.StartsWith($manifestVer))

    Write-Host "`n== the skill actually LOADS (validate would not catch this) ==" -ForegroundColor Cyan
    $list = claude plugin list --json | ConvertFrom-Json
    $ours = $list | Where-Object { $_.id -like 'flaui-mcp@*' }
    Check 'our plugin is listed'  ($null -ne $ours)
    Check 'our plugin is enabled' ($ours.enabled -eq $true)

    # M3: this also settles whether --json lists skills-dir plugins at all. The earlier check was
    # VACUOUS (none were installed), not negative. If our plugin is absent here while the files are
    # on disk, --json does NOT list skills-dir plugins -> `status` must keep reading the filesystem,
    # and this assertion needs a different mechanism. Report it either way; do not silently adapt.
    if ($null -eq $ours) {
        Write-Warning "M3: --json did not list our skills-dir plugin though its files exist. RECORD THIS in the spec."
    }

    Write-Host "`n== uninstall ==" -ForegroundColor Cyan
    & $Exe uninstall --agent claude | Out-Host
    Check 'skill removed' (-not (Test-Path $root))
}
finally {
    Pop-Location
    Remove-Item Env:CLAUDE_CONFIG_DIR, Env:FLAUI_MCP_STATE_DIR, Env:FLAUI_MCP_DATA_DIR, Env:FLAUI_MCP_AGY_PLUGINS_DIR -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
}

if ($failed.Count) { Write-Host "`n$($failed.Count) CHECK(S) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nALL CHECKS PASSED" -ForegroundColor Green
```

- [ ] **Step 2: Run it**

Run:
```powershell
dotnet publish src/FlaUI.Mcp.Server -c Release -o publish
./scripts/install-smoke.ps1
```
Expected: `ALL CHECKS PASSED`, exit 0.

- [ ] **Step 3: Record M3**

Whatever the M3 warning says — fired or not — record the outcome in the spec's `⚠ INCONCLUSIVE` note, replacing the prediction with the measurement.

- [ ] **Step 4: Commit**

```bash
git add scripts/install-smoke.ps1 docs/superpowers/specs/2026-07-16-skill-distribution-design.md
git commit -m "test(install): local smoke asserting the skill loads outside the repo; record M3"
```

---

### Task 13: The collision smoke — the negative assertion

**Why:** Task 12 proves our skill loads. It **cannot** prove the disable worked, because both plugins load silently. This is the gate for that, and it needs a profile seeded with the colliding copy.

**Files:**
- Modify: `scripts/install-smoke.ps1`

- [ ] **Step 1: Add the collision scenario**

Append to `scripts/install-smoke.ps1`, immediately before the `if ($failed.Count)` line:

```powershell
Write-Host "`n== collision: a seeded marketplace copy must end up DISABLED ==" -ForegroundColor Cyan
$sandbox2 = Join-Path ([IO.Path]::GetTempPath()) "flaui-smoke2-$([guid]::NewGuid())"
$claude2  = Join-Path $sandbox2 'claude'
$state2   = Join-Path $sandbox2 'state'
$outside2 = Join-Path $sandbox2 'outside'
New-Item -ItemType Directory -Force -Path $claude2, $state2, $outside2 | Out-Null
$env:CLAUDE_CONFIG_DIR   = $claude2
$env:FLAUI_MCP_STATE_DIR = $state2
$env:FLAUI_MCP_DATA_DIR  = Join-Path $sandbox2 'data'
Push-Location $outside2
try {
    claude plugin marketplace add ckir/flauimcp 2>&1 | Out-Host
    claude plugin install flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Host

    $before = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
    # A gate that cannot run has NOT passed. Counting the skip as a failure is the whole point:
    # otherwise the one scenario this gate exists for is the one it silently stops checking.
    Check 'the marketplace copy could be seeded (the gate is able to run at all)' ($null -ne $before)
    if ($null -eq $before) {
        Write-Warning "could not seed the marketplace copy — the collision gate did NOT run. This is a FAILURE, not a skip."
    } else {
        Check 'seeded marketplace copy starts enabled' ($before.enabled -eq $true)

        & $Exe install --agent claude | Out-Host

        $after = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
        # THE NEGATIVE. Without this the gate passes when the disable never ran.
        Check 'marketplace copy is DISABLED after install' ($after.enabled -eq $false)

        & $Exe uninstall --agent claude | Out-Host
        $restored = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
        Check 'marketplace copy is RESTORED after uninstall' ($restored.enabled -eq $true)
        Check 'the marker was consumed' (-not (Test-Path (Join-Path $state2 'disabled-plugins.json')))
    }
}
finally {
    claude plugin uninstall flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Null
    claude plugin marketplace remove flaui-mcp 2>&1 | Out-Null
    Pop-Location
    Remove-Item Env:CLAUDE_CONFIG_DIR, Env:FLAUI_MCP_STATE_DIR, Env:FLAUI_MCP_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $sandbox2 -ErrorAction SilentlyContinue
}
```

- [ ] **Step 2: Run it**

Run: `./scripts/install-smoke.ps1`
Expected: `ALL CHECKS PASSED`.

A collision block that could not run is a **FAILURE, not a skip** — and the script now enforces that
rather than merely saying so in prose. *(The panel caught the first draft doing exactly what this
paragraph warned against: warning about a skip while exiting 0 and printing `ALL CHECKS PASSED`. A
gate whose own bypass is silent is not a gate.)*

- [ ] **Step 3: Commit**

```bash
git add scripts/install-smoke.ps1
git commit -m "test(install): assert the NEGATIVE — the seeded marketplace copy must be disabled"
```

---

### Task 14: Rewrite the falsified docs

**Why:** Mechanism A and the README rewrite **must land in the same change**. Shipping the bundle while `README.md:158-163` still advertises the marketplace — or retiring the marketplace while the README advertises it — breaks the documented path a second time. And `ops-manual.md:22-24` states a promise the remedy breaks.

**Files:**
- Modify: `docs/ops-manual.md:22-24`, `:27`, `:45-47`
- Modify: `README.md:158-163`, `:165-166`, `:173`, `:306-311`

- [ ] **Step 0: Verify state**

Confirm `docs/ops-manual.md:22-24` still reads "on uninstall performs **targeted key removal** — it only deletes FlaUI.Mcp's own entries and leaves your other settings intact." If not, STOP: `STATE_MISMATCH`.

- [ ] **Step 1: Fix the broken promise**

The breach is **the verb, not the jurisdiction**: `flaui-mcp@flaui-mcp` *is* a FlaUI.Mcp entry (our marketplace, our namespace), but **re-enabling it is an active write, not a deletion** — the sentence describes a purely-subtractive lifecycle the product no longer has.

Replace `docs/ops-manual.md:21-23`:

```markdown
The configuration is **idempotent** (safe to re-run), writes **atomically** with a
timestamped backup of each file it touches, and on uninstall performs **targeted cleanup and state
restoration** — it deletes FlaUI.Mcp's own entries and re-enables any conflicting FlaUI.Mcp plugin
that the installer disabled. It never touches settings that are not ours.
```

- [ ] **Step 2: Fix the "what the installer changes" table**

Replace the Claude Code row at `docs/ops-manual.md:27`:

```markdown
| **Claude Code** | (via `claude mcp add/remove` CLI) + `~/.claude/skills/flaui-mcp/` (honors `CLAUDE_CONFIG_DIR`) | Registers the `flaui-mcp` MCP server **and** deploys the driving skill, which Claude auto-loads as `flaui-mcp@skills-dir`. If a conflicting `flaui-mcp@flaui-mcp` plugin from the old marketplace is present, it is **disabled** (reversibly — uninstall re-enables it) and the fact is recorded in `%LOCALAPPDATA%\FlaUI.Mcp\state\`. Nothing is deployed if the `claude` CLI is not on PATH. |
```

- [ ] **Step 3: Note the uninstall dialog**

Replace `docs/ops-manual.md:45-47`:

```markdown
- **Via the installer:** uninstall "FlaUI.Mcp" from **Settings → Apps** (or
  Add/Remove Programs). The uninstaller reverts every agent's config (targeted removal), re-enables
  any conflicting plugin it disabled, and removes the files. If any of that cleanup did not
  complete, it shows you what failed before it finishes — the executable is deleted, so
  `flaui-mcp status` cannot tell you afterwards.
```

- [ ] **Step 4: Rewrite the README install path**

Replace `README.md:158-163` (verify the exact current text first) with:

```markdown
### Claude Code

The installer does it for you. `flaui-mcp install` registers the MCP server **and** deploys the
driving skill to `~/.claude/skills/flaui-mcp/`, which Claude Code auto-loads as `flaui-mcp@skills-dir`.
The skill is versioned with the binary, so it always describes the tools you actually have.

Restart Claude Code after installing — plugins load at session start, so a running session keeps the
previous skill until it restarts.

**Installed Claude Code after flaui-mcp?** Nothing was deployed then (correctly — there was no client
to register). Run `flaui-mcp install --agent claude`, and check with `flaui-mcp status`.

**Upgrading from v0.14.x?** If you installed the plugin from the old marketplace, the installer
disables that copy so two versions of the driving skill cannot both load. It is reversible: uninstall
re-enables it. `flaui-mcp status` reports what was disabled.
```

- [ ] **Step 5: Delete the two autotrain promises**

`README.md:165-166` claims "it adds only the driving **and self-improvement** skills" and `:173` claims "**Claude Code gets the full self-improving plugin**". The autotrain loop is **cut** from this scope — the payload is driving-only. Remove the self-improvement claims from both lines, leaving the driving-skill statement intact.

- [ ] **Step 6: Fix the maintainer note's plugin id**

`README.md:306-311` names the right file but the wrong identity: `flaui-mcp@flaui-mcp` is a **marketplace** id, so the documented incantation would silently disable nothing. Replace the JSON with:

```json
{ "enabledPlugins": { "flaui-mcp@skills-dir": false } }
```

Add, in prose: the equivalent is `claude plugin disable flaui-mcp@skills-dir --scope local`, run from the repo root. **Do not document `--scope project`** — it writes the git-tracked `.claude/settings.json` and would commit a maintainer's personal disable into the repo for every cloner.

- [ ] **Step 7: Verify no stale references survive**

Run:
```bash
rg -n "flaui-mcp@flaui-mcp|self-improving|marketplace add" README.md docs/ops-manual.md
```
Expected: every remaining hit is deliberate (the v0.14.x upgrade note, and the marketplace section if it is being kept). Anything else is a doc that confidently describes the old behavior — worse than no doc.

- [ ] **Step 8: Commit**

```bash
git add README.md docs/ops-manual.md
git commit -m "docs: bundle the Claude skill, correct the broken uninstall promise and plugin ids"
```

---

### Task 15: Version lockstep and release prep

**Why:** The version bump is for **lockstep and diagnosis** (`flaui-mcp status` reports the deployed version) — **not** to defeat a cache. That justification was measured false: skills-dir plugins load live from disk and appear nowhere in `~/.claude/plugins/cache/`. The stale-cache hazard is a *marketplace* property.

**⚠ A release does not ship the plugin fix — a merge does.** `.github/workflows/release.yml` contains **zero** plugin/marketplace references, and the marketplace sources from `./plugins/flaui-mcp` inside the git repo. Any task phrased as "the plugin fix ships in v0.15.0's setup.exe" is wrong on the mechanism.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj:30`
- Modify: `installer/flaui-mcp.iss:4`
- Modify: `plugins/flaui-mcp/.claude-plugin/plugin.json`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Bump all three in lockstep**

`FlaUI.Mcp.Server.csproj` — `<Version>0.14.0</Version>` → `<Version>0.15.0</Version>`
`installer/flaui-mcp.iss:4` — `#define AppVersion "0.14.0"` → `"0.15.0"`
`plugins/flaui-mcp/.claude-plugin/plugin.json` — `"version": "0.14.0"` → `"0.15.0"`

- [ ] **Step 2: Verify lockstep**

Run:
```bash
rg -n '0\.(14|15)\.0' src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss plugins/flaui-mcp/.claude-plugin/plugin.json
```
Expected: three hits, all `0.15.0`.

- [ ] **Step 3: Write the changelog entry**

Add to `CHANGELOG.md` under a new `## 0.15.0` heading:

```markdown
### Added
- Claude Code now gets the driving skill from the installer, versioned with the binary. It no longer
  has to be installed by hand from the marketplace, and it can no longer drift from your server build.
- `flaui-mcp status` reports the Claude skill and any conflicting plugin the installer disabled.

### Fixed
- The v0.14.0 plugin manifest declared a `hooks` key that Claude Code already auto-loads by
  convention, so the plugin **failed to load entirely**. Everyone who followed the README got no skills.
- Upgrading from v0.14.x no longer leaves two copies of the driving skill loaded at once. The old
  marketplace copy is disabled (reversibly — uninstall re-enables it) and reported in `status`.
- A `claude` CLI that hangs can no longer hang Setup: every invocation is now time-bounded.
```

- [ ] **Step 4: Full gate**

Run: `dotnet build -c Debug` — expected **0 Warning(s), 0 Error(s)**
Run: `dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"` — expected **Failed: 0**
Run: `dotnet publish src/FlaUI.Mcp.Server -c Release -o publish && ./scripts/install-smoke.ps1` — expected `ALL CHECKS PASSED`

The publish step matters on its own: it proves the embedded seed survives **single-file publish**, which is the packaging the release actually ships.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss plugins/flaui-mcp/.claude-plugin/plugin.json CHANGELOG.md
git commit -m "release: v0.15.0 (bundled Claude driving skill + collision remedy)"
```

---

## Deliberately NOT in this plan

| Item | Why | Where it goes |
|---|---|---|
| **Retiring the marketplace** (`plugins/` + `.claude-plugin/marketplace.json`) | **OPEN — user-deferred.** Both endpoints are coherent. Note the retire argument is only valid in the **future tense**: today the marketplace is the sole documented way a Claude Code user gets the skill. Also note retiring it does **not** resolve the collision — v0.14.x users already have their copy. | A user decision, then its own change |
| Making `--purge-data` honor `--agent` | Rewrites shared router semantics (`CliRouter.cs:18`, `:103-110`) for a **pre-existing, unrelated** behavior. Moving our own marker is the smaller contained fix. If its agent-blindness is a defect, it is a **separate** one — file it, don't smuggle it. | A separate issue |
| Deleting `flaui-curate`'s USER mode | Unreachable dead code once the curator is never shipped — but deleting it is a **one-way door**. | Open (#2), user-owned |
| The autotrain loop / global observation inbox | **Cut** on 7 independent nails from 2 models. Do not resurrect without answering them — in particular that a global inbox **voids the anti-poisoning gate**, opening a path from an untrusted terminal buffer into the GROWTH region and out to every user as operating instructions. | A future subproject |
| The Stop hook's PowerShell rewrite | Moot under a driving-only payload — no hook ships. Returns the moment autotrain bundling does. | With autotrain |
| A CI gate for any of this | **Measured impossible:** no Claude CLI, no `~/.claude` in CI. Do not plan a gate that cannot exist. | Local/manual only |

## Plan review ledger

**Panel — agy, 2026-07-16, `relentless-adversarial-auditor`. Verdict: REJECT, 7 findings — 5 VALID
AND FOLDED, 2 REJECTED ON MEASUREMENT.** Seats: Literal Implementer · Type & Signature Pedant · Test
Adequacy Auditor · Cascade Analyst.

| # | Seat | Finding | Disposition |
|---|---|---|---|
| 1 | Literal Implementer | `ReadDeployedVersion` is undefined — Task 10 calls it but no task defines it, "and it cannot be the agy parser since the Claude manifest lives in a different directory" | **REJECTED — measured false.** It **already exists** at `InstallStatus.cs:51`, in the very class Task 10 modifies. It takes the manifest **path as a parameter**, so it is location-agnostic and the stated reasoning does not hold. *(Clarified in Task 10 anyway — the plan never said it was pre-existing, and that ambiguity was real.)* |
| 2 | Literal Implementer | `BeginErrorReadLine()` without an `ErrorDataReceived` handler "throws `InvalidOperationException` immediately… this will crash the installer on every single process invocation" | **REJECTED — measured false.** Ran it: `RedirectStandardError = true` + `BeginErrorReadLine()` + no handler → **no throw**, exit 0, stdout intact. A confident, specific, and wrong claim. **But the remedy direction was right for a different reason:** discarding stderr loses *why* a command failed, so a handler was added and stderr is now kept **on failure only** (keeping it on success would corrupt the `--json` output we parse). |
| 3 | Type & Signature Pedant | `CollisionMarker.Record` returns `void`, yet its own catch comment claims the failure "becomes a warning at the call site" — so a marker we cannot write is **silently swallowed** | **FOLDED — VALID.** A real contract lie, and a severe one: we have *already disabled the user's plugin* by then, so a lost record means uninstall never restores it. `Record` now returns `string?` and `Apply` surfaces it. |
| 4 | Test Adequacy | the smoke *says* "a SKIPPED collision block is not a pass" but, when seeding fails, only `Write-Warning`s — adding nothing to `$failed`, then exiting 0 and printing `ALL CHECKS PASSED` | **FOLDED — VALID.** A gate that fails open, in the exact scenario the gate exists for. The skip is now a `Check` that fails. *(The prose warned against precisely what the code did.)* |
| 5 | Test Adequacy | `merged.Contains(e)` uses default record equality — case-**sensitive** — on `ProjectPath`, a **Windows** path | **FOLDED — VALID.** And it was worse than reported: `Apply`'s "already recorded?" check was case-sensitive while `Restore`'s `Same()` was case-**insensitive** — the two disagreed. One `CollisionMarker.SameEntry` now serves both. A casing mismatch would have made a re-install wrongly conclude **the user** disabled it. |
| 6 | Cascade | `if (r.Change == AgentChange.NotFound) return r;` aborts the whole Claude block, so an uninstall with `claude` off PATH **bypasses `Restore()`** — marker orphaned, user's plugin left disabled, warning never emitted | **FOLDED — VALID; the round's best finding.** The gate belongs to **install only**. Uninstall now always removes the skill (pure file I/O — no CLI needed) and always attempts the restore. |
| 7 | Cascade | the Inno hook shows the `MsgBox` conditionally on `LoadStringFromFile` but **deletes unconditionally** — an unreadable log is destroyed having shown nobody, violating the plan's own "reaped only once a human has seen it" | **FOLDED — VALID.** Unreadable → report the path and **keep** the file. |

**Found while folding #6 — the panel did not reach it.** `Restore()` consumed the marker even when
`TryReadInventory` failed outright. With the CLI absent that meant: cannot restore, *and* the record
is deleted — the user's plugin disabled forever with nothing anywhere saying we did it. **Restore now
returns early and KEEPS the marker when the inventory cannot be read**, with per-entry manual recourse
in the warning. R7's stale-marker hazard is about surviving a *successful* consume; this is not one.

**Method note.** Both rejections were **confident, specific, and false** — a fabricated missing symbol
and a fabricated exception, each with a plausible mechanism attached. Both took one measurement to
kill. Neither was dismissed on judgment. The 5 that survived measurement were all real, and 2 of them
(#3, #6) were paths to **permanently losing a user's plugin** — the exact class of defect this whole
remedy exists to prevent, reintroduced by the code meant to prevent it.

### Amendment during execution (2026-07-16) — Tasks 3, 6, 7 rewritten after a Task 2 measurement

Executing Task 2 (a pure measurement) **falsified the plan's original detection step.** MEASURED:
`claude plugin list --json`'s `enabled` field is **CWD-resolved for `scope=local` rows** (proven three
ways, incl. a planted-and-flipped `settings.local.json`; one affected `projectPath` no longer exists on
disk). A single global read from Setup's cwd reports every local copy as `enabled=false`, so the
original "one global read, act on `enabled`" would **silently skip a live local collision** — the exact
failure this remedy exists to prevent. This retroactively **vindicates round 7's rejected "`--json` is
CWD-contextual" finding**, which had been dismissed after measuring only the stable fields
(id/scope/projectPath) and never varying CWD against `enabled`.

**Resolution — AGY-FIRST consult + one negotiation turn (both models converged) + user-decided all
scopes.** See [Settled decisions](../specs/2026-07-16-skill-distribution-design.md) #4. Changes:
- **Task 6 `Apply` → mutation-as-detector.** No longer branches on `e.Enabled` (unreliable). Enumerates
  rows by id from the global list, then attempts `disable` at each row's own scope+cwd; `exit 0` ⇒ we
  transitioned it (record); `exit 1` ⇒ **re-read `list --json` at cwd=projectPath** (new `ReadEnabledAt`)
  to tell already-off from a real failure — **never by parsing the message string** (agy proposed the
  string-match; claude rejected it as UI-text-not-contract; agy conceded). Added a `Directory.Exists`
  guard (injected, defaults to `Directory.Exists`) skipping rows whose project is gone.
- **Task 6 tests rewritten** around a *faithful* `FakeClaude` that reproduces the measured semantics
  (CWD-resolved `enabled`, `disable` exit-1-when-already-off, phantom-`enable`). Includes a **regression
  test that fails against the old read-based `Apply`** and a meta-test proving the fake reproduces the
  CWD bug (so the regression is not vacuous). 13 → 16 tests.
- **Task 3 docstring corrected** — the "identical output from any working directory" claim was false for
  `enabled`; the parser is now documented as a faithful transcriber whose non-user `Enabled` is valid
  only when read at the row's own projectPath.
- **Task 7 `Restore` unchanged** (it never read `enabled` — matches on presence). Its two round-trip
  fakes were made faithful (model `disable` exit 1 when already-off); one assertion changed from "zero
  calls" to the real invariant "no `enable` call + nothing recorded", because mutation-as-detector now
  *attempts* a no-op disable even on a user-already-disabled entry.
- **Rejected during the consult:** per-project reads (load-bearing on the leaky abstraction),
  direct settings-JSON writes (couples to Claude's private storage), and user-scope-only (assumes scope
  instead of responding to inventory).

**Not yet code-verified.** This amendment is plan text. Its build/logic verification happens when the
Task 6 implementer transcribes and runs it (TDD: tests already written, implement until green). A failed
transcription is a STOP-and-report signal, not a silent adapt.

**AGY-AFTER panel on the amendment — 2 rounds, 7 findings, all folded.** `relentless-adversarial-auditor`;
solo floor (Axiom Breaker, Cascade Analyst, State Corruptor, Protocol Pedant, Mechanism Gamer, Literal
Implementer) + agy escalation, then a rotation round (added Blindspot Auditor, Dependency Cynic).
- **R1 solo:** `LooksLikeEmptyList` missing `StringComparison` (0-warning-gate risk; measured precedent
  `ConfigArgsMerge.cs:17`) · the `stillEnabled == null` "could not verify" branch was untested (added
  `FailListAtCwd` + a test). Verified compilable via `ImplicitUsings enable` in the server csproj.
- **R1 agy:** `Restore` lacked the `Directory.Exists` guard that `Apply` has ⇒ a project deleted between
  install and uninstall would print an impossible "run it from <deleted path>". agy framed it as a crash;
  **measured false** — `Process.Start` on a missing WD throws `Win32Exception`, which `ProcessRunner`
  catches → the defect is the bad recourse, not a crash. Guard + test added.
- **R2 agy (rotation):** (1) the CLI-missing early-return in `Restore` bypassed that same guard in its
  manual-recourse text → same bad recourse, fixed + test; (2) on a marker-write failure `Apply` both
  promised and rescinded a restore in one concatenated line → banner now withheld when `Record` failed,
  + test; (3) `TryReadInventory` leaked the raw `-2`/`-1` sentinels to the operator → translated;
  (4) `LooksLikeEmptyList`'s `StartsWith("[")` treated ANY `[`-prefixed output as empty, so a
  schema-drift array parsing to zero rows was a **silent skip of a live collision** → now `JsonNode.Parse
  is JsonArray { Count: 0 }` + test. agy's mechanism for (4) ("a new field breaks Parse") was **wrong**
  (Parse tolerates unknown fields); the real trigger is a *renamed* field, and the fix is stricter than
  agy's proposed `== "[]"` (which would false-warn on `[ ]`).
- Test counts after folding: Task 6 → 19, Task 7 → 13.
- **Round 3 is the hard cap** — round 2 still found substance, so continuing requires an operator decision.

## Risks this plan accepts

| Risk | Standing |
|---|---|
| **Never verified on a virgin machine** (no `~/.claude`). Every observation is on an initialized dev profile. | **The weakest evidence in the whole design.** Task 12's sandbox narrows it but does not close it — the sandbox still runs on a machine where Claude Code has run before. |
| `skills-dir` auto-load is lesser-documented than marketplace install and could change under us | Pin to a tested Claude Code version; Task 12 is the tripwire. Fallback is marketplace-driven install. |
| Fixing a skill typo now needs an installer release | Accepted deliberately — the price of lockstep. |
| Session-lifetime skew: a live Claude session keeps the old skill against the new binary | Bounded by a restart. **Documented, not claimed away** (Task 14 Step 4). On-disk lockstep is the guarantee; bundling does **not** make skew impossible in a live session. |
