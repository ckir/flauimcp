# Activation via MCP Server Instructions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Deliver the install-time activation decision to the agent over the MCP handshake on Claude Code,
pin the skill description that is agy's only activation channel, and delete the dead installer code this
work exposed.

**Architecture:** `ActivationPayload.Text` splits into a client-agnostic `Core` (served as
`McpServerOptions.ServerInstructions`) and a Claude-Code-specific `Addendum` — the `ToolSearch` load block
**and the `driving-flaui-mcp` skill pointer**, both of which name mechanisms a generic MCP client does not
have (spec D1). The addendum stays in the SessionStart hook. `Text` is recomposed as `Core + Addendum`, so there is one source
of truth and no drift. Because agy was **measured** to receive `InitializeResult.instructions` and drop
it, the skill frontmatter is agy's only channel and gets its own pinning test. The dead
`ClaudeSkillDeployer.Deploy()` and `AgyConfigWriter.Install()` paths are removed, closing ROADMAP 15.

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.19041.0`), xUnit, ModelContextProtocol.Core 1.4.0.

**Branch:** `activation-via-server-instructions`, already created at `f4bac46`.

---

## Ground rules for this branch

- **Warnings are errors repo-wide.** Every build must report `0 Warning(s) 0 Error(s)`.
- **The solution is `FlaUI.Mcp.slnx`.** There is no `.sln`.
- **Never pass `--no-build`** to `dotnet test` — a deleted test still runs from a stale DLL.
- **Every new gate needs a LOGIC mutant** that turns *that specific test* red, and you must confirm the
  test **RAN and went red** — not that the build failed. For a source-sweep test the mutant must be a
  **commented-out variant**, not a deletion, because this repo has shipped a comment-blind sweep three
  times.
- Commit after every task.

## The measurement this plan's value rests on — and how to re-check it

Part A is **inert on agy** and Part B exists **only** because of one measured result: agy receives
`InitializeResult.instructions` and does not surface it. That is an observation about a live tool, not a
law, and tools update themselves.

**Deliberately NOT pinned to a version.** House policy is to assume latest and react when something
breaks, so this plan records provenance and a re-run recipe rather than a version constraint. Observed
**2026-08-22** against the then-current agy (`agy --version` reported `1.1.18` at the time — recorded as
provenance, *not* as a supported-version claim).

**If agy ever does surface server instructions, nothing here breaks** — Part A simply starts helping a
second client, and Part B's pin becomes belt-and-braces rather than the only guard. So this is a
"re-check when curious", not a gate on execution. To re-run it: stand up a throwaway stdio MCP server that
puts an unguessable sentinel in its `initialize` result's `instructions` field **and logs every JSON-RPC
method it receives**; register it with `agy mcp add`, ask a fresh `agy -p` session to quote any
connection-time server guidance, then `agy mcp remove` it. The log is the control that distinguishes
"dropped" from "never connected" — without it a negative result proves nothing.

⚠ The harness used for the original measurement lived in a session scratchpad and is **disposable** — do
not go looking for it; the recipe above is the durable artifact.

## Verified facts this plan rests on

Every citation below was read or grepped against the working tree at `f4bac46` before this plan was
written.

| Fact | Where |
|---|---|
| `.AddMcpServer()` takes no options today | `src/FlaUI.Mcp.Server/Program.cs:214` |
| Payload prose was **973 chars** of **1100** before the split; **after** the corrected split it is **1074 / 1100** (26 headroom) and **10 / 15** lines | measured from `ActivationPayload.Text` |
| The three "dead" test files hold **7 tests covering LIVE paths**: 4 `Remove_*` + 3 `Uninstall_*` | `grep -n "public void "` on each |
| Surviving tests ARRANGE via the doomed methods (`d.Deploy()`, `w.Install(...)`) | `ClaudeSkillDeployerTests.cs:98`, `AgyConfigWriterTests.cs:62`, `AgySkillDeployTests.cs:65,81` |
| `ClaudeSkillDeployer.Deploy()` called only from tests | `grep -rn "\.Deploy()" src/ test/` |
| `ClaudeSkillDeployer.Remove()` is LIVE | `src/FlaUI.Mcp.Server/Install/CliRouter.cs:319` |
| `ClaudeSkillDeployer.SkillRoot` is LIVE | `src/FlaUI.Mcp.Server/Install/InstallStatus.cs:36` |
| `AgyConfigWriter.Uninstall()` is LIVE (legacy sweep) | `src/FlaUI.Mcp.Server/Install/CliRouter.cs:298` |
| `AgyConfigWriter.Install()` called only from tests | `grep -rn "AgyConfigWriter" src/` → only `:298` |
| `McpServerEntry`, `ConfigArgsMerge`, `JsoncFile` are used by other writers and do NOT become dead | `grep -rln` each, 4-5 files apiece |
| Shipped skill is embedded from `.claude/skills/driving-flaui-mcp/SKILL.md` | `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj:13-15` |
| `SkillLoadLineTests.BothCopies()` covers the build input + repo twin | `test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs:14-19` |
| All three skill twins are 36946 bytes | `wc -c` on each |
| `DescribeSeed` needs `{root}/skills/driving-flaui-mcp/SKILL.md` + `{root}/plugin.json` | `InstallStatus.cs:183-193` |
| `DescribeClaudeSkill` needs `{skillRoot}/skills/driving-flaui-mcp/SKILL.md` | `InstallStatus.cs:127-132` |

## File structure

| File | Change | Responsibility after |
|---|---|---|
| `src/FlaUI.Mcp.Server/Install/ActivationPayload.cs` | Modify | Owns `Core`, `Addendum`, and `Text = Core + Addendum` |
| `src/FlaUI.Mcp.Server/Program.cs` | Modify (`:213-216`) | Configures the server with `ServerInstructions = ActivationPayload.Core` |
| `test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs` | Modify | Budgets + the Core/Addendum contract |
| `test/FlaUI.Mcp.Tests/Install/ServerInstructionsWiringTests.cs` | **Create** | Structural sweep pinning the `Program.cs` wiring line |
| `test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs` | Modify | Adds the behavioral-core pin over both skill copies |
| `src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs` | Modify | `Remove()` + `SkillRoot` only; `Deploy()` gone |
| `src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs` | Modify | `Uninstall()` only; both `Install()` overloads + `DeploySkill()` gone |
| `test/FlaUI.Mcp.Tests/Install/ClaudeSkillDeployerTests.cs` | **Prune** (→9 to 4) | Keeps the four `Remove_*` tests — `Remove()` is LIVE |
| `test/FlaUI.Mcp.Tests/Install/AgyConfigWriterTests.cs` | **Prune** (3→1) | Keeps `Uninstall_removes_both_and_preserves_other_permissions` |
| `test/FlaUI.Mcp.Tests/Install/AgySkillDeployTests.cs` | **Prune** (5→2) | Keeps the two `Uninstall_*` tests |
| `test/FlaUI.Mcp.Tests/Install/InstallStatusTests.cs` | Modify (`:19-22`) | Builds its fixture directly instead of via deleted APIs |
| `test/FlaUI.Mcp.Tests/Install/InstallStatusClaudeTests.cs` | Modify (`:48`) | Same |
| `ROADMAP.md` | Modify | Item 15 closed |

---

## Part A — Serve the activation core over the MCP handshake

### Task 1: Split `ActivationPayload` into `Core` and `Addendum`

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ActivationPayload.cs:33-45`
- Test: `test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs`

**On the duplication this creates — already decided, in the spec.** After Task 2 a Claude Code user
receives the core twice: once via `ServerInstructions` at connect, once inside `Text` from the SessionStart
hook (`PluginArtifactWriter.cs:170` wires that hook to `"{exePath}" activation-payload`). Spec **D3**
accepts this deliberately, for a reason worth repeating here: *"The hook fires on `startup`, `clear` and
`compact`. `InitializeResult.Instructions` is delivered once, at connection. A `/clear` or a compaction
resets context and does not re-run initialize."* Shrinking the hook to the addendum would lose the
prohibition and the triggers at the first `/clear`. **Do not "optimise" the duplication away.**

⚠ **A deliberate, stated behaviour change.** Recomposing `Text` as `Core + Addendum` moves the three-line
load block from positions 4-6 to positions 7-9. Nothing is added or removed from the payload — only
reordered — and Step 1 pins that by asserting all eight original lines still appear verbatim. The
alternative (keeping a hand-ordered `Text` beside `Core`) permits silent drift between the two, which is
worse than a reorder.

- [ ] **Step 1: Write the failing tests**

Add to `test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs`:

```csharp
    /// The CORE is what a non-Claude client receives over `InitializeResult.instructions`. It must carry
    /// the behavioural rules and must NOT carry Claude Code's ToolSearch incantation, which means nothing
    /// to another client.
    [Fact]
    public void Core_carries_the_behavioural_rules_and_no_client_specific_mechanism()
    {
        Assert.Contains("Never ask the user", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("you can see and operate this Windows desktop yourself", ActivationPayload.Core,
            StringComparison.Ordinal);
        Assert.Contains("Triggers:", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("need a lease", ActivationPayload.Core, StringComparison.Ordinal);

        // Spec S6 names BOTH halves and says why: "assert it does NOT contain ToolSearch or
        // driving-flaui-mcp. This is the test that would have caught D1 being skipped." Omitting the
        // second half is exactly how the skill pointer leaks into a generic client's instructions.
        Assert.DoesNotContain("ToolSearch", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.DoesNotContain("driving-flaui-mcp", ActivationPayload.Core, StringComparison.Ordinal);
    }

    /// D2, the "addendum orphan" guard. If the Claude Code hook fails, an agent gets the CORE and never
    /// the ADDENDUM. Without a client-agnostic loading sentence it would be told it can drive the desktop,
    /// try a deferred tool, fail, and have no recovery instruction — worse than today's silence.
    [Fact]
    public void Core_tells_the_agent_tools_may_need_loading_without_naming_one_clients_mechanism()
    {
        Assert.Contains("may need loading", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolSearch", ActivationPayload.Core, StringComparison.Ordinal);
    }

    [Fact]
    public void Addendum_carries_the_claude_code_load_block()
    {
        Assert.Contains("ToolSearch \"select:", ActivationPayload.Addendum, StringComparison.Ordinal);
        Assert.Contains("Load the tools (one call):", ActivationPayload.Addendum, StringComparison.Ordinal);
        // D1: the skill pointer is client-specific and belongs HERE, not in the core.
        Assert.Contains("driving-flaui-mcp", ActivationPayload.Addendum, StringComparison.Ordinal);
    }

    /// Text is COMPOSED from the two halves, so they cannot drift apart.
    [Fact]
    public void Text_is_exactly_core_then_addendum()
        => Assert.Equal(ActivationPayload.Core + "\n" + ActivationPayload.Addendum, ActivationPayload.Text);

    /// The split REORDERS the payload (the load block moves to the end) but must not LOSE anything.
    ///
    /// ⚠ ONE original line is deliberately NOT asserted verbatim: the lease line carried BOTH a core
    /// concern (the lease boundary) and an addendum one (the skill pointer), so D1 forces it to be SPLIT
    /// across the two halves. Its two halves are asserted separately below. Every other line survives
    /// byte-for-byte.
    [Fact]
    public void The_split_preserves_every_original_payload_line()
    {
        var original = new[]
        {
            "flaui-mcp is installed: you can see and operate this Windows desktop yourself.",
            "Never ask the user to look at, read, or click inside a desktop app on your behalf, and do not infer UI state indirectly from process lists.",
            "Triggers: what is on screen; is an app running or responding; what a background terminal/console tab shows; clicking, typing or filling a GUI dialog; confirming a change landed in the real app.",
            "Load the tools (one call):",
            "If that returns no matches, retry ToolSearch \"desktop window snapshot\" and use ONLY: desktop_list_windows, desktop_open_window, desktop_snapshot, desktop_get_text, desktop_input_status. If one is absent, say so — never substitute a similar name.",
            "Read-only perception needs no lease and cannot disturb the user: desktop_list_windows(includeHandles:true) then desktop_snapshot wN then desktop_get_text wN eN.",
        };
        foreach (var line in original)
            Assert.Contains(line, ActivationPayload.Text, StringComparison.Ordinal);

        // The split lease line: both halves must still be there, on their respective sides.
        Assert.Contains("all need a lease", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("driving-flaui-mcp skill", ActivationPayload.Addendum, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ActivationPayloadTests"`
Expected: FAIL to **compile** — `'ActivationPayload' does not contain a definition for 'Core'`. That is
the expected failure for this step; the behavioural mutant proof comes in Step 5.

- [ ] **Step 3: Implement the split**

In `src/FlaUI.Mcp.Server/Install/ActivationPayload.cs`, replace the whole `Text` declaration (currently
`:33-45`) with:

```csharp
    /// <summary>The client-agnostic half — capability, prohibition, triggers, and the lease boundary.
    /// This is what `Program.cs` serves as `McpServerOptions.ServerInstructions`, so it must never name a
    /// mechanism only one client has.
    ///
    /// ⚠ MEASURED 2026-08-22: agy RECEIVES `InitializeResult.instructions` and DOES NOT surface it (a
    /// sentinel probe with a live-connection control came back `NONE-VISIBLE`). So this half reaches
    /// Claude Code and is inert on agy — agy's channel is the driving skill's frontmatter, pinned by
    /// SkillLoadLineTests. Do not "simplify" by assuming every client reads this.</summary>
    public static readonly string Core = string.Join("\n", new[]
    {
        "flaui-mcp is installed: you can see and operate this Windows desktop yourself.",
        "Never ask the user to look at, read, or click inside a desktop app on your behalf, and do not infer UI state indirectly from process lists.",
        "Triggers: what is on screen; is an app running or responding; what a background terminal/console tab shows; clicking, typing or filling a GUI dialog; confirming a change landed in the real app.",
        // D2 — the "addendum orphan" guard. Mechanism-free ON PURPOSE: a client that is not Claude Code
        // has no ToolSearch, and naming one here would be noise at best and a wrong instruction at worst.
        "These tools may need loading before they can be called - use your client's tool-discovery mechanism.",
        "Read-only perception needs no lease and cannot disturb the user: desktop_list_windows(includeHandles:true) then desktop_snapshot wN then desktop_get_text wN eN.",
        // The lease BOUNDARY is a core safety rule. The pointer to the skill that implements it is
        // client-specific and lives in the Addendum (spec D1) - so the original single line is SPLIT.
        "Typing, clicking, dragging, or reading a BACKGROUND terminal tab all need a lease.",
    });

    /// <summary>The Claude-Code-specific half: the concrete deferred-tool load call and its fallback.
    /// Stays in the SessionStart hook and is NOT served over MCP.</summary>
    public static readonly string Addendum = string.Join("\n", new[]
    {
        // FIRST, deliberately: in the recomposed Text this line lands immediately after the core's
        // "...all need a lease." line, so "those" keeps its referent. Moving it to the END of the
        // addendum silently re-points "those" at the read-only tools named in the fallback line - tools
        // the core has just said need NO lease. Order is load-bearing here, not cosmetic.
        "For those, use the driving-flaui-mcp skill.",
        "Load the tools (one call):",
        LoadLine,
        "If that returns no matches, retry ToolSearch \"desktop window snapshot\" and use ONLY: desktop_list_windows, desktop_open_window, desktop_snapshot, desktop_get_text, desktop_input_status. If one is absent, say so — never substitute a similar name.",
    });

    /// <summary>The SessionStart payload: both halves, composed. Composing rather than hand-ordering is
    /// what makes drift between Core and Text impossible.</summary>
    public static readonly string Text = Core + "\n" + Addendum;
```

⚠ **Field order matters.** `Core`, `Addendum`, and `Text` are `static readonly` fields initialised in
declaration order, and `Addendum` reads `LoadLine`. Keep all three **after** the existing `LoadLine`
declaration (`:29-32`), or `Text` initialises from a null `LoadLine` at runtime with no compiler error.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ActivationPayloadTests"`
Expected: PASS, all tests. In particular `Prose_stays_within_the_injected_text_budget` must still pass.
**Measured against the corrected split** (D2 sentence added, lease line split in two): prose lands at
**1074 / 1100** — 26 chars of headroom — and **10 / 15** lines. Verified separately that the resulting
core contains neither `ToolSearch` nor `driving-flaui-mcp`, so both spec-S6 assertions hold.

- [ ] **Step 5: Prove the new gate is not vacuous (LOGIC mutant)**

Edit `ActivationPayload.cs` and change the D2 line to remove the phrase the test looks for:

```csharp
        "These tools are ready to call.",
```

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ActivationPayloadTests"`
Expected: `Core_tells_the_agent_tools_may_need_loading_without_naming_one_clients_mechanism` **RAN and
FAILED**. Confirm that named test went red — not merely that the run was non-zero.

Then revert the line **by rewriting it in place** with the correct text. Do **not** restore from a `.bak`
copy: `os.replace` of a backup preserves the backup's mtime, MSBuild skips the rebuild, and the mutant
assembly survives the revert. Re-run the filter and confirm PASS before continuing.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ActivationPayload.cs test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs
git commit -m "feat(activation): split the payload into a client-agnostic Core and a Claude Code Addendum"
```

### Task 2: Serve `Core` as `ServerInstructions`

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Program.cs:213-216`

- [ ] **Step 1: Make the change**

Replace exactly this (`Program.cs:213-216`):

```csharp
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

with:

```csharp
builder.Services
    // The install-time approval decision travels over the MCP handshake, not only in the Claude Code
    // SessionStart hook. The hook is absent between install and client relaunch, and absent silently if
    // it fails; `initialize` happens on every connect. Serving the CORE (never Text) keeps Claude Code's
    // ToolSearch incantation out of clients that have no such mechanism.
    .AddMcpServer(options => options.ServerInstructions = FlaUI.Mcp.Server.Install.ActivationPayload.Core)
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

- [ ] **Step 2: Build**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/FlaUI.Mcp.Server/Program.cs
git commit -m "feat(activation): serve the activation core as MCP ServerInstructions"
```

### Task 3: Pin the wiring so it cannot be silently dropped

**Files:**
- Create: `test/FlaUI.Mcp.Tests/Install/ServerInstructionsWiringTests.cs`

**Why a source sweep and not a behavioural assertion.** `Program.cs` is top-level statements that build
and immediately `RunAsync()` a stdio server; a headless test cannot construct that host and read back its
`McpServerOptions` without starting the server on stdio. Asserting on a locally-constructed
`McpServerOptions` would test the test's own setup, and asserting `ActivationPayload.Core` is non-empty
tests a constant. So this is labelled **structural**, exactly as `DenylistShutterGuardTests` is, and it
must strip comments — this repo has shipped a comment-blind sweep three times.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// STRUCTURAL guard, not behavioural. It asserts the wiring LINE exists in Program.cs, because the
/// server is a stdio host that a headless test cannot start and interrogate. It is deliberately
/// comment-stripped: commenting the line out is the cheapest way to silently un-ship this feature, and a
/// naive `Contains` would still pass on a commented-out copy.
public class ServerInstructionsWiringTests
{
    /// Strips COMMENTS ONLY - deliberately, and this is the interesting decision in the file.
    ///
    /// An earlier revision also stripped string literals, to stop a stray
    /// `var x = "...ActivationPayload.Core";` from spoofing the gate. **That cure was measurably worse
    /// than the disease.** Sequential regex cannot strip comments and strings safely in either order,
    /// because each sweep corrupts the other's delimiters. All three variants were measured:
    ///
    ///   comments-then-strings : a single `"http://localhost/"` anywhere in the file has its closing
    ///                           quote eaten by the comment sweep; the string sweep then runs away from
    ///                           the orphaned quote to the next quote in the file, swallowing the
    ///                           AddMcpServer block. The gate fails on a VALID file.
    ///   strings-then-comments : a comment containing one `"` (e.g. `// the " character`) starts the
    ///                           same runaway. Also fails on a valid file.
    ///   comments-only         : survives BOTH, and still catches the commented-out mutant.
    ///
    /// So: comments only. The accepted limit is that a string literal containing the whole anchored call
    /// shape would satisfy this test. That is contrived, and it is not the threat model - this gate
    /// exists to catch the wiring being DELETED or COMMENTED OUT, which is what actually happens. A
    /// correct comment+string strip needs a real parser (Roslyn), which is not worth a dependency for
    /// one assertion.
    private static string ProgramSourceWithoutComments()
    {
        var src = File.ReadAllText(RepoPaths.At("src", "FlaUI.Mcp.Server", "Program.cs"));
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);   // block comments
        src = Regex.Replace(src, @"//[^\r\n]*", "", RegexOptions.Multiline);   // line comments
        return src;
    }

    /// Anchored on the AddMcpServer CALL, not on a bare assignment anywhere in the file. Anchoring is
    /// the second half of the anti-spoof guard: the assignment must appear inside the configuration
    /// lambda that actually reaches the server.
    [Fact]
    public void Program_configures_the_server_with_the_activation_core()
    {
        var src = ProgramSourceWithoutComments();
        Assert.Matches(
            @"AddMcpServer\s*\(\s*\w+\s*=>\s*\w+\.ServerInstructions\s*=\s*(FlaUI\.Mcp\.Server\.Install\.)?ActivationPayload\.Core",
            src);
    }

    /// Serving Text would push Claude Code's ToolSearch load line at clients that have no ToolSearch.
    [Fact]
    public void Program_serves_the_core_and_never_the_whole_payload()
    {
        var src = ProgramSourceWithoutComments();
        Assert.DoesNotMatch(@"ServerInstructions\s*=\s*(FlaUI\.Mcp\.Server\.Install\.)?ActivationPayload\.Text", src);
    }
}
```

- [ ] **Step 2: Confirm `RepoPaths` exists and exposes `At(params string[])`**

Run: `rg -n "static string At" test/FlaUI.Mcp.Tests/`
Expected: a match in the shared test helper (`SkillLoadLineTests.cs:17` already calls
`RepoPaths.At(rel.Split('/'))`). If the signature differs, match the existing call shape rather than
inventing one.

- [ ] **Step 3: Run the test**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ServerInstructionsWiringTests"`
Expected: PASS (Task 2 already added the line).

- [ ] **Step 4: Prove it is not vacuous — the COMMENTED-OUT mutant**

This is the mutant that matters. In `Program.cs`, comment out the wiring line and restore the bare call:

```csharp
builder.Services
    // .AddMcpServer(options => options.ServerInstructions = FlaUI.Mcp.Server.Install.ActivationPayload.Core)
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ServerInstructionsWiringTests"`
Expected: `Program_configures_the_server_with_the_activation_core` **RAN and FAILED**. If it passes, the
comment stripping is broken — fix the sweep, not the test's expectation.

Revert by rewriting the block in place, re-run, confirm PASS.

- [ ] **Step 5: Prove the SECOND assertion too — it is weaker than it looks**

⚠ `Program_serves_the_core_and_never_the_whole_payload` is a `DoesNotMatch`, so **it also passes when the
wiring line is deleted entirely.** It guards against one specific wrong value, not against absence — the
first assertion is what guards absence. Prove it catches the value it exists to catch:

In `Program.cs`, change `ActivationPayload.Core` to `ActivationPayload.Text` in the wiring line.

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ServerInstructionsWiringTests"`
Expected: `Program_serves_the_core_and_never_the_whole_payload` **RAN and FAILED**.

Revert to `.Core` by rewriting in place, re-run, confirm PASS.

- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Install/ServerInstructionsWiringTests.cs
git commit -m "test(activation): pin the ServerInstructions wiring with a comment-stripped sweep"
```

---

## Part B — Pin agy's only activation channel

### Task 4: Pin the driving skill's behavioural core

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs`

**Why this task exists.** agy was measured to drop `InitializeResult.instructions`, so Part A is inert
there. agy's activation channel is the `description:` frontmatter of `driving-flaui-mcp/SKILL.md`, which
is what a skill-matching client reads to decide the skill applies. Today that sentence is pinned by
**nothing** — `rg "rather than asking the user" src/ test/` returns zero matches — so an ordinary
"make this read better" edit removes agy's only framing with every test green. The build input is the
copy embedded into the shipped artifact (`FlaUI.Mcp.Server.csproj:13-15`), and `BothCopies()` already
covers it.

- [ ] **Step 1: Write the failing tests**

Add to `test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs`:

```csharp
    /// The frontmatter block between the opening and closing `---` fences.
    private static string Frontmatter(string rel)
    {
        var text = Read(rel);
        var m = Regex.Match(text, @"\A---\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
        Assert.True(m.Success, $"{rel}: no YAML frontmatter block found");
        return m.Groups[1].Value;
    }

    /// ⚠ THE LOAD-BEARING TEST FOR agy. MEASURED 2026-08-22: agy receives `InitializeResult.instructions`
    /// and does NOT surface it, so ActivationPayload.Core never reaches it. This description is agy's
    /// ONLY activation channel. Rewording it freely is how the channel disappears silently.
    ///
    /// It pins the BEHAVIOURAL CORE, not one sentence: the anti-delegation rule, the trigger conditions
    /// that make a skill-matching client fire at all, and the lease boundary. Pinning only the
    /// prohibition would leave the other two unguarded on the one client that has nothing else.
    ///
    /// If you are changing the wording deliberately, change this test in the same commit and say why.
    [Theory]
    [MemberData(nameof(BothCopies))]
    public void The_description_carries_the_activation_behavioural_core(string rel)
    {
        var fm = Frontmatter(rel);

        Assert.Contains("rather than asking the user to observe or operate their desktop for you", fm,
            StringComparison.Ordinal);
        Assert.Contains("what is on the Windows screen", fm, StringComparison.Ordinal);
        Assert.Contains("under a lease", fm, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify it passes against the current wording**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SkillLoadLineTests"`
Expected: PASS for both copies. If it fails, the twins have drifted — fix by `cp` from
`.claude/skills/driving-flaui-mcp/SKILL.md`, which is the build input, and re-run.

- [ ] **Step 3: Prove it is not vacuous (LOGIC mutant)**

In `.claude/skills/driving-flaui-mcp/SKILL.md` only, reword the description's prohibition clause to:

```
description: Use when you need to know what is on the Windows screen, whether a desktop app is running or responding, what a background terminal or console tab shows, or when you need to click, type into, or confirm a change landed in a real GUI app. Use the installed flaui-mcp desktop_* tools. Covers read-only perception, synthetic input under a lease, and focus/ref recovery.
```

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SkillLoadLineTests"`
Expected: `The_description_carries_the_activation_behavioural_core` **RAN and FAILED for the build-input
copy** and passed for the repo twin — which also demonstrates the theory covers both copies
independently.

Revert by rewriting the description line in place, re-run, confirm PASS. Then verify the twins are
byte-identical again.

⛔ **Do NOT use `wc -c` for this.** It measures LENGTH, not identity: a same-length typo made while
retyping the description — a swapped character, a substituted letter — corrupts the file and passes a
byte-count check silently. That is precisely the mutation this step just made and reverted by hand, so it
is the realistic failure, not a contrived one. Compare the bytes:

```bash
cmp .claude/skills/driving-flaui-mcp/SKILL.md plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md \
  && cmp .claude/skills/driving-flaui-mcp/SKILL.md publish/plugin/skills/driving-flaui-mcp/SKILL.md \
  && echo "all three identical"
```

Expected: `all three identical`, with no `differ` line. `cmp` is silent and returns 0 on a match. If a
twin differs, fix it by copying the build input over it (`cp`) rather than retyping — retyping is how they
drifted in the first place.

- [ ] **Step 4: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs
git commit -m "test(activation): pin the driving skill description - agy's only activation channel"
```

---

## Part C — Delete the dead installer code (closes ROADMAP 15)

Order matters: rework the two tests that use the doomed APIs as *fixtures* first, so the suite is green at
every commit.

### Task 5: Rework the two `InstallStatus` fixtures to stop using the doomed APIs

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Install/InstallStatusTests.cs:17-22`
- Modify: `test/FlaUI.Mcp.Tests/Install/InstallStatusClaudeTests.cs:45-48`

Both tests call `Deploy()` / `Install()` purely to create directories on disk. `InstallStatus` only reads
files, so the fixture can write them directly — which is also a better test, because it no longer couples
a status assertion to an installer's behaviour.

- [ ] **Step 1: Add a shared local fixture helper to `InstallStatusTests.cs`**

```csharp
    /// Write exactly what InstallStatus READS, rather than calling an installer to produce it.
    /// DescribeSeed (InstallStatus.cs:183-193) needs the SKILL.md plus plugin.json for the version;
    /// DescribeClaudeSkill (:127-132) needs only the legacy SKILL.md.
    private static void WriteSeedPlugin(string pluginRoot, string version)
    {
        var skillDir = Path.Combine(pluginRoot, "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");
        File.WriteAllText(Path.Combine(pluginRoot, "plugin.json"),
            "{\n  \"name\": \"flaui-mcp\",\n  \"version\": \"" + version + "\"\n}\n");
    }

    private static void WriteLegacyClaudeSkill(string claudeConfigDir)
    {
        var dir = Path.Combine(claudeConfigDir, "skills", "flaui-mcp", "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");
    }
```

⚠ The legacy Claude path is `{claudeConfigDir}/skills/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` — the
doubled `skills` segment is real: `SkillRoot` is `{claudeConfigDir}/skills/flaui-mcp`
(`ClaudeSkillDeployer.cs:26`) and `DescribeClaudeSkill` appends `skills/driving-flaui-mcp/SKILL.md`
(`InstallStatus.cs:129`). Do not "fix" it.

- [ ] **Step 2: Replace the fixture calls in `InstallStatusTests.Reports_a_deployed_seed_with_its_version`**

Replace these three lines (`:19-22`):

```csharp
        new AgyConfigWriter(Path.Combine(dataDir, "s.json"), Path.Combine(dataDir, "p.json"), plugins)
            .Install(@"C:\flaui-mcp.exe");
        new ClaudeSkillDeployer(claude).Deploy();   // deploy both skills so nothing reads "NOT deployed"
```

with:

```csharp
        WriteSeedPlugin(Path.Combine(plugins, "flaui-mcp"), "9.9.9");
        WriteLegacyClaudeSkill(claude);   // both present so nothing reads "NOT deployed"
```

⚠ **No version helper is needed, despite the test's name.**
`Reports_a_deployed_seed_with_its_version` asserts only `Contains("deployed")`,
`DoesNotContain("NOT deployed")` and the path (`InstallStatusTests.cs:29-31`) — **nothing reads the
version back**. A literal keeps the fixture independent of assembly versioning, so
`WriteSeedPlugin(..., "9.9.9")` above is deliberate and sufficient.

- [ ] **Step 3: Replace the fixture call in `InstallStatusClaudeTests`**

Replace `:48`:

```csharp
        new ClaudeSkillDeployer(claude).Deploy();
```

with:

```csharp
        var legacyDir = Path.Combine(claude, "skills", "flaui-mcp", "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");
```

Ensure `using System.IO;` is present at the top of that file; add it if not.

- [ ] **Step 4: Run both suites**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~InstallStatus"`
Expected: PASS. The assertions themselves are unchanged, so a failure here means the fixture writes the
wrong path — compare against `InstallStatus.cs:129` and `:185`.

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Install/InstallStatusTests.cs test/FlaUI.Mcp.Tests/Install/InstallStatusClaudeTests.cs
git commit -m "test(install): build InstallStatus fixtures directly instead of via the installers"
```

### Task 6: Delete `ClaudeSkillDeployer.Deploy()`

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs:28-58`
- Delete: `test/FlaUI.Mcp.Tests/Install/ClaudeSkillDeployerTests.cs`

`Remove()` and `SkillRoot` are LIVE (`CliRouter.cs:319`, `InstallStatus.cs:36`) and must survive.

- [ ] **Step 1: Delete the method and its now-unused member**

Delete the entire `Deploy()` method, matched by its signature `public string? Deploy()`, together with its
`/// <summary>Returns null on success…` doc comment. Then delete the `SkillResource` constant, which
`Deploy()` was the only reader of.

⚠ **Delete by member, not by line range** — see the warning in Task 7 Step 1 for why. **`Remove()` and
`SkillRoot` must survive** (`CliRouter.cs:319` and `InstallStatus.cs:36`). Verify:

```bash
rg -n "public string\? Remove\(\)|public string SkillRoot" src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs
```

Expected: both still present.

Verify before deleting the constant:

```bash
rg -n "SkillResource" src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs
```

If the only remaining match is its own declaration, delete it. If anything else reads it, leave it and say
so in the commit message.

- [ ] **Step 2: PRUNE the test file — do NOT delete it**

⛔ **`ClaudeSkillDeployerTests.cs` tests BOTH halves of the class, and `Remove()` is LIVE production code
(`CliRouter.cs:319`).** Deleting the file would silently strip coverage from a shipped uninstall path.
Delete only the five tests that exercise the removed `Deploy()`:

- `Deploy_writes_the_manifest_and_the_skill`
- `The_manifest_is_claude_shaped_and_carries_the_assembly_version`
- `The_deployed_skill_is_the_embedded_seed`
- `Deploy_is_idempotent_and_overwrites_a_stale_skill`
- `Deploy_failure_returns_a_warning_and_never_throws`

**KEEP these four**, which cover the live `Remove()`:

- `Remove_deletes_the_skill_tree`
- `Remove_on_a_machine_that_never_had_it_is_a_silent_no_op`
- `Remove_survives_an_undeletable_tree_and_says_so`
- `Remove_leaves_other_skills_alone`

- [ ] **Step 2b: Rework the survivors' ARRANGE, which currently calls the deleted method**

`Remove_deletes_the_skill_tree` arranges with `d.Deploy()` (`:98`), and other `Remove_*` tests do the same.
Add this helper to the class and replace every remaining `Deploy()` call with it:

```csharp
    /// Writes exactly the tree Deploy() used to write, so the Remove_* tests keep a real target after
    /// Deploy() is gone. Deliberately NOT a re-implementation of Deploy(): these tests assert what
    /// Remove() DELETES, so the arrange only has to put files where Remove() looks.
    private static void SeedSkillTree(string claudeConfigDir)
    {
        var root = new ClaudeSkillDeployer(claudeConfigDir).SkillRoot;
        var skillDir = Path.Combine(root, "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(skillDir);
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            "{\n  \"name\": \"flaui-mcp\",\n  \"version\": \"9.9.9\"\n}\n");
    }
```

Find every call site to replace:

```bash
rg -n "\.Deploy\(\)" test/FlaUI.Mcp.Tests/Install/ClaudeSkillDeployerTests.cs
```

Expected after the rework: **no matches**. Each was an arrange call of the form `d.Deploy();` and becomes
`SeedSkillTree(cfg);` — note the helper takes the CONFIG DIR, not the deployer.

- [ ] **Step 2c: REPLACE the one guarantee this deletion destroys**

⛔ **`The_deployed_skill_is_the_embedded_seed` is the ONLY test anywhere that checks the CONTENT of a
shipped `SKILL.md`.** Measured: a repo-wide grep for any other test reading a deployed/staged `SKILL.md`
and comparing it to source returns **nothing**. Deleting it leaves this hole:

- `SkillLoadLineTests` pins the **repo copies** — it never looks at what the installer actually emits.
- `CliRouterPluginRegistrationTests` pins that the staged `SKILL.md` **exists** — not what is in it.

So a botched embed — wrong resource name, a truncated extract, a zero-byte file — would ship an empty
skill, every test would stay green, and **agy's only activation channel would be silently gone.** That is
precisely the channel Part B exists to protect, so Part C must not quietly widen the hole.

Add this assertion to the existing `Install_generates_staging_artifacts_and_writes_no_agent_config_file`
test in `test/FlaUI.Mcp.Tests/Install/CliRouterPluginRegistrationTests.cs`, immediately after the existing
`Assert.True(File.Exists(Path.Combine(staging, "skills", "driving-flaui-mcp", "SKILL.md")));`:

```csharp
        // Existence is not enough. This is the ONLY check that what the installer EMITS matches the
        // source of truth. The shipped skill's frontmatter is agy's only activation channel (agy drops
        // ServerInstructions - measured), so a truncated or empty extract silently removes it while
        // SkillLoadLineTests, which reads the REPO copies, stays green.
        Assert.Equal(
            File.ReadAllText(RepoPaths.At(".claude", "skills", "driving-flaui-mcp", "SKILL.md")),
            File.ReadAllText(Path.Combine(staging, "skills", "driving-flaui-mcp", "SKILL.md")));
```

⚠ Compare against `.claude/skills/...` specifically — that is the copy the csproj embeds
(`FlaUI.Mcp.Server.csproj:13-15`), so it is the true source. Comparing against `plugins/...` would test a
twin rather than the build input.

- [ ] **Step 2d: Prove THAT gate is not vacuous either**

Temporarily change the embedded-resource logical name in `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`
from `FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md` to
`FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md.bak`.

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CliRouterPluginRegistrationTests"`
Expected: the test **RAN and FAILED** — either on the new content assertion or on the extract throwing.
Either is a valid red; what matters is that a broken embed can no longer pass. Revert the csproj by
rewriting the line in place, re-run, confirm PASS.

- [ ] **Step 3: Build and confirm nothing else referenced it**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: `0 Warning(s) 0 Error(s)`. A CS0103/CS1061 here means a live caller exists that the grep missed
— stop and report it rather than restoring `Deploy()`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(install): delete dead ClaudeSkillDeployer.Deploy() and its suite (ROADMAP 15)"
```

### Task 7: Delete both `AgyConfigWriter.Install()` overloads and `DeploySkill()`

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs`
- Delete: `test/FlaUI.Mcp.Tests/Install/AgyConfigWriterTests.cs`
- Delete: `test/FlaUI.Mcp.Tests/Install/AgySkillDeployTests.cs`

`Uninstall()` is LIVE (`CliRouter.cs:298`) and must survive, along with everything it reads:
`JsoncFile`, `McpServerEntry.ServerName`, `Permission`, `RemoveSkill()`, `Detail()`, `PluginRoot`,
`_serversPath`, `_permsPath`, `_pluginsDir`.

- [ ] **Step 1: Delete these members, in this order**

⛔⛔ **DELETE BY MEMBER, NEVER BY LINE RANGE.** An earlier draft of this plan cited ranges, and two of
them were wrong in a way that would have silently deleted LIVE code: `DeploySkill()` ends at `:60` but the
draft said `:36-80`, which spans `RemoveSkill()`'s declaration at `:68`; and it said Install-overload-2 was
`:105-127`, which spans `Detail()`'s declaration at `:127`. **`RemoveSkill()` and `Detail()` are both
called by the live `Uninstall()`.** Ranges in a plan go stale the moment anything above them moves — match
on the signature, delete the whole member including its doc comment, and verify afterwards.

Delete each of these complete members from `src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs`, each together
with its own `///` doc comment:

1. `public AgentResult Install(string exePath, IReadOnlyList<string>? args = null)`
2. `public AgentResult Install(string exePath, IReadOnlyList<string> addArgs, IReadOnlyList<string> removeArgs)`
3. `private string? DeploySkill()`
4. `private bool EnsurePermission()` — read only by the two `Install` overloads
5. `private static string[] ReadArgs(JsonObject? entry)` — read only by `Install` overload 2
6. `private const string SkillResource = …` — read only by `DeploySkill()`

⚠ **These members MUST SURVIVE. Check each one is still present after you delete:**

| Member | Why it survives |
|---|---|
| `public AgentResult Uninstall()` | called at `CliRouter.cs:298` |
| `private string? RemoveSkill()` | called by `Uninstall()` |
| `private string Detail(string? skillWarning)` | called by `Uninstall()` |
| `PluginRoot`, `Permission`, `_serversPath`, `_permsPath`, `_pluginsDir` | read by the three above |

```bash
rg -n "Uninstall\(\)|RemoveSkill\(\)|Detail\(|PluginRoot" src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs
```

Expected: all four still present. If any is missing, you deleted too much — restore and redo by signature.

C# does **not** error on an unreachable private method, so the compiler will not find 4-6 for you. Verify
each before deleting:

```bash
rg -n "EnsurePermission|ReadArgs|SkillResource|ConfigArgsMerge" src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs
```

Expected after the deletions: no matches at all. `ConfigArgsMerge` appears in that list because overload 2
was this file's only user — the class itself stays, since `CliRouter` and `GenericMcpConfigWriter` use it.

- [ ] **Step 2: Keep `Detail()` — it is still live**

`Uninstall()` calls `Detail(skillWarning)` at `:168`. Do not delete it. Its `skillWarning is null` branch
still matters, because `RemoveSkill()` still returns a warning.

- [ ] **Step 3: PRUNE the two test files — do NOT delete them**

⛔ **Both files test `Uninstall()`, which is LIVE (`CliRouter.cs:298`).** One of the survivors is
`Uninstall_removes_both_and_preserves_other_permissions` — already tracked as **ROADMAP item 20** for
flakiness, so deleting it would also erase a filed defect's only reproducer.

From `AgyConfigWriterTests.cs` delete **only**:

- `Install_writes_both_mcpServers_and_permission_allow`
- `Install_does_not_duplicate_the_permission_on_rerun`

**KEEP** `Uninstall_removes_both_and_preserves_other_permissions`.

From `AgySkillDeployTests.cs` delete **only**:

- `Install_deploys_agy_driving_skill_plugin`
- `Skill_deploy_failure_warns_but_still_registers_the_server`
- `Successful_install_reports_the_skill_directory`

**KEEP** `Uninstall_removes_the_agy_plugin_folder` and
`Uninstall_survives_an_undeletable_skill_dir_and_says_so`.

- [ ] **Step 3b: Rework the three survivors' ARRANGE**

All three arrange with `w.Install(@"C:\...")` (`AgyConfigWriterTests.cs:62`, `AgySkillDeployTests.cs:65`
and `:81`).

⚠ **Put this helper in ONE shared file, not a copy in each test class.** Two copies of a fixture that must
stay in sync is the same drift shape this plan rejects for `Core`/`Text` two parts earlier — it would be
inconsistent to argue single-source-of-truth for the payload and then paste a helper twice. The repo
already has the precedent: `test/FlaUI.Mcp.Tests/Install/RepoPaths.cs` is a shared, non-test helper class
in this exact directory. Create `test/FlaUI.Mcp.Tests/Install/AgyFixtures.cs` beside it holding a single
`internal static class AgyFixtures` with this method, and call `AgyFixtures.SeedAgyInstall(...)` from both
test classes:

```csharp
    /// Writes exactly what Uninstall() looks for: the mcpServers entry, the permission token, and the
    /// plugin dir with a file in it. Replaces the old `w.Install(...)` arrange now that Install is gone.
    ///
    /// ⚠ INTERNAL, not private: it is called from AgyConfigWriterTests and AgySkillDeployTests, and a
    /// `private` member here fails to compile at those call sites with CS0122.
    internal static void SeedAgyInstall(string serversPath, string permsPath, string pluginsDir)
    {
        File.WriteAllText(serversPath,
            "{ \"mcpServers\": { \"flaui-mcp\": { \"command\": \"C:\\\\x\\\\flaui-mcp.exe\" } } }");

        var perms = JsoncFile.Load(permsPath);
        var permissions = perms["permissions"] as JsonObject;
        if (permissions is null) { permissions = new JsonObject(); perms["permissions"] = permissions; }
        var allow = permissions["allow"] as JsonArray;
        if (allow is null) { allow = new JsonArray(); permissions["allow"] = allow; }
        allow.Add("mcp(flaui-mcp/*)");
        JsoncFile.Save(permsPath, perms);

        var skillDir = Path.Combine(pluginsDir, "flaui-mcp", "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");
    }
```

⚠ **`SeedAgyInstall` OVERWRITES the servers file.** `Uninstall_removes_both_and_preserves_other_permissions`
seeds `perms` with `command(git status)` BEFORE the arrange (`:57`) and asserts it survives — so this
helper deliberately MERGES into the permissions file rather than overwriting it. Preserve that ordering:
write the pre-existing permission first, then call the helper. If you overwrite `perms`, the test still
passes for the wrong reason, because the token it checks for would never have existed.

Find every call site to replace:

```bash
rg -n "\.Install\(" test/FlaUI.Mcp.Tests/Install/AgyConfigWriterTests.cs test/FlaUI.Mcp.Tests/Install/AgySkillDeployTests.cs
```

Expected after the rework: **no matches**.

- [ ] **Step 4: Build, then run the full headless gate**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: `0 Warning(s) 0 Error(s)`.

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: all pass. **Never add `--no-build`** — a deleted test still runs from a stale DLL, which would
show these files' tests "passing" after deletion.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(install): delete dead AgyConfigWriter.Install()/DeploySkill() and their suites"
```

### Task 8: Close ROADMAP 15

**Files:**
- Modify: `ROADMAP.md` (the item-15 row of the decomposition table)

- [ ] **Step 1: Update the row**

Find the row beginning `| 15 | ` and replace its status cell, keeping the existing description intact.

⛔ **The replacement must be ONE LINE.** This is a GitHub-flavoured-Markdown table row; a literal newline
inside a cell terminates the row and breaks the table for every entry below it. Wrap nothing, hard-break
nothing:

```
✅ shipped — both dead paths deleted on the activation branch (`ClaudeSkillDeployer.Deploy()`, both `AgyConfigWriter.Install()` overloads, `DeploySkill()`) along with the tests that covered only them. `Remove()`, `SkillRoot` and `Uninstall()` are LIVE and survived, so their tests were PRUNED rather than deleted; two `InstallStatus` tests that used the deleted APIs as fixtures now write the files directly.
```

- [ ] **Step 2: Verify no sibling item was filed by mistake**

Run: `rg -n "^### 23\." ROADMAP.md`
Expected: no match. The operator's decision was to clear the category here, not to file a sibling.

- [ ] **Step 3: Commit**

```bash
git add ROADMAP.md
git commit -m "docs(roadmap): close item 15 - both dead installer paths deleted"
```

---

## Task 9: Full gates

- [ ] **Step 1: Clean build**

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental`
Expected: `0 Warning(s)` `0 Error(s)`. `--no-incremental` is deliberate: an incremental build does not
re-report warnings for up-to-date projects (this is ROADMAP item 12).

- [ ] **Step 2: Headless gate**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: all pass, `0 skipped`.

**Use a falsifiable count, not a vibe.** "Expect a lower total" cannot distinguish the intended pruning
from an accidental extra deletion. The arithmetic, from counts measured at `f4bac46`:

| | Tests |
|---|---|
| Deleted (`Deploy_*` / `Install_*` / `Skill_deploy_*` / `Successful_install_*`) | **-10** |
| Retained in those same files (4 `Remove_*` + 3 `Uninstall_*`) | **0** (unchanged) |
| Added (Task 1: 5 · Task 3: 2 · Task 4: 2 theory cases) | **+9** |
| **Net** | **-1** |

Record the actual number. If the delta is not **-1**, stop: something was deleted or added that this plan
did not call for.

- [ ] **Step 3: Desktop gate**

⚠ **Main thread, quiet machine, physical console.** Do not co-run a subagent — measured, it produced four
spurious failures and a 40% longer run. `SendInput` does not deliver over RDP (check `qwinsta`, not
`$env:SESSIONNAME`), and `0 skipped` needs a user-granted lease.

⚠ **RUN IT IN THE BACKGROUND, and do NOT set a timeout on it.** This suite takes **~12 minutes (~720s)**,
which is longer than the **600s maximum** a foreground shell call accepts — so a foreground run, or a
backgrounded one given an explicit 600s timeout, is **killed before it finishes** and reports an
infrastructure timeout that looks like a gate failure. Backgrounding is what removes the cap; it is not a
cap of its own. Read the earlier sentence that way if it seems to say otherwise.

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category=Desktop&Category!=KnownDefect&Category!=Measurement&FullyQualifiedName!~PopupGrafting"`
Expected: all pass. Item 16 and item 20 are known flakes here — if one fails, re-run it in isolation
before treating it as a regression.

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PopupGrafting"`
Expected: 1 passing.

- [ ] **Step 4: Manual acceptance — confirm the instructions actually arrive**

This is the only check that proves the feature end to end, and no automated test can do it.

⛔⛔ **A `/mcp` reconnect alone CANNOT observe this change, and skipping this warning would make a
working feature look broken.** The generated `.mcp.json` sets `command = exePath`
(`PluginArtifactWriter.cs:50`) and the SessionStart hook runs `"{exePath}" activation-payload`
(`:170`) — **both point at the INSTALLED binary**, not at your build output. Reconnecting relaunches the
old exe, which has none of this branch's changes.

1. Build the branch: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental`
2. **Install the built binary** so the client actually runs it.
   ⚠ **This mutates real client state.** `FLAUI_MCP_*` env overrides do **not** sandbox the agy half:
   `AgyPluginRegistrar.Register()` shells out to the real `agy` CLI, which ignores them — this is
   **ROADMAP 22**, and it has already cost one manual restore. Prefer `--agent claude` to keep the blast
   radius on the client you are actually verifying, and snapshot `agy mcp list` first if you use
   `--agent all`.
3. Reconnect the MCP client (`/mcp` in Claude Code).
4. In a **new** session, confirm an `MCP Server Instructions` section now lists `flaui-mcp` and carries
   the CORE text.

⚠ Expect it to be **absent on agy** — that is the measured behaviour, not a bug in this change. Do not
"fix" it by serving `Text`, which would only push a ToolSearch instruction at a client that has no
ToolSearch.

---

## Self-review

**Spec coverage.** §D1 (CORE/ADDENDUM split) → Task 1. §D2 (client-agnostic loading sentence, the
addendum-orphan guard) → Task 1 Steps 1 and 3. §D3b (delivered at connect) → recorded in the Task 2
comment and the Task 9 Step 4 reconnect instruction. §D4 (budget) → Task 1 Step 4, with the measured
1074/1100 figure. §D5 (a client dropping the field is undetectable) → Task 9 Step 4's warning, and it is
why Part B exists at all. §5 "the server advertises the core" → Task 3, implemented as the structural
sweep the spec explicitly permits when a headless test cannot reach the value. §7's measured agy result →
Part B in full.

**Known gaps, stated rather than hidden.**

1. **Task 3 is structural, not behavioural.** It proves the wiring line is present, not that a client
   received the text. Task 9 Step 4 is the behavioural check and it is manual. This is the spec's own
   accepted limit (D5), not an oversight.
2. **`InstallStatus` reports the agy seed from `AgyPluginsDir`** (`InstallStatus.cs:30`), while the modern
   install registers through the agy CLI, which chooses its own directory. They agree today only because
   both default to `~/.gemini/config/plugins` — an agreement nothing enforces. **Out of scope here**;
   it belongs with ROADMAP 22.
3. **The reorder in Task 1** changes the SessionStart payload's line order. Pinned as
   lossless by `The_split_preserves_every_original_payload_line`, but it is a real change to a proven
   artifact and is called out rather than slipped in.
4. **No test asserts the `publish/plugin` twin.** `BothCopies()` covers the build input and the repo twin;
   the third copy is checked only by the `wc -c` step in Task 4 Step 3. Pre-existing, not introduced here.

**Placeholder scan.** No TBD/TODO. Every code step carries complete code; every command carries its
expected output; both mutants are specified concretely, and the source-sweep mutant is a commented-out
variant per the standing rule.

**Type consistency.** `ActivationPayload.Core` / `.Addendum` / `.Text` are `public static readonly string`
throughout Tasks 1-3. `WriteSeedPlugin(string, string)`, `WriteLegacyClaudeSkill(string)` and
are defined once in Task 5 Step 1 and used with matching arity in Steps 2-3. `SeedSkillTree(string)`
(Task 6 Step 2b) takes the CONFIG DIR and derives `SkillRoot` itself; `SeedAgyInstall(string, string,
string)` (Task 7 Step 3b) takes servers/perms/plugins in that order, matching the existing
`TempPaths()` tuple those tests already destructure.

## Round 1 panel — folded findings

Seven findings folded; one refuted by measurement and deliberately NOT folded.

| # | Seat | Finding | Fold |
|---|---|---|---|
| 1 | agy Axiom Breaker | Plan put the `driving-flaui-mcp` pointer in the CORE, violating spec D1, and wrote the spec-mandated guard with the `driving-flaui-mcp` half missing | Lease line SPLIT across Core/Addendum; both `DoesNotContain` assertions added |
| 2 | agy Cascade Analyst | Tasks 6/7 deleted whole test files that also cover the LIVE `Remove()` / `Uninstall()` paths — 7 tests, one of them ROADMAP 20's only reproducer | Changed to PRUNE + arrange rework, with the survivors enumerated by name |
| 3 | Blindspot Auditor | Task 9 Step 4 could not observe the feature: both channels run the INSTALLED exe | Build + install steps added, with the ROADMAP 22 warning |
| 4 | Mechanism Gamer | The second wiring assertion is a `DoesNotMatch` that passes when the feature is absent, and had no mutant | Its own mutant added (Task 3 Step 5) |
| 5 | Cascade Analyst | "Expect a lower total" is unfalsifiable | Replaced with a measured net of **-1** and a stop condition |
| 6 | Literal Implementer | `ThisVersion()` was dead weight — nothing asserts the version | Dropped for a literal |
| 7 | Axiom Breaker | The core/hook duplication was unstated | Cited spec D3, which already accepted it — for a better reason than the plan had (`/clear` does not re-run initialize) |
| — | Activation Auditor | Suspected the frontmatter regex breaks on a UTF-8 BOM | **REFUTED by measurement** — file starts `2d 2d 2d`. Not folded. |

### Round 2 — folded findings

| # | Seat | Finding | Fold |
|---|---|---|---|
| 8 | Axiom Breaker | Architecture header still called the Addendum "the `ToolSearch` load block" — stale the instant round 1 moved the skill pointer into it | Rewritten. **A fold spawns its own edges.** |
| 9 | State Corruptor | Plan pasted `SeedAgyInstall` into BOTH agy test files — the same drift shape it rejects for `Core`/`Text` | Single shared `AgyFixtures.cs`, following the `RepoPaths.cs` precedent |
| 10 | Dependency Cynic | The plan's central premise is a measured agy behaviour with no provenance and no re-run recipe | Provenance + recipe added. **Deliberately NOT version-pinned** — house policy is assume-latest and react when something breaks |
| 11 | agy Axiom Breaker | "Budget ~12 minutes; background it against a 600s cap" reads as a guaranteed timeout | Partially valid: backgrounding REMOVES the 600s foreground cap, so there is no built-in failure — but the wording invites exactly that error. Reworded explicitly. |
| 12 | agy Dependency Cynic | `wc -c` verifies LENGTH, not identity — a same-length typo passes silently | Replaced with `cmp`. Correct finding; I should have caught it. |
| — | State Corruptor | Suspected `JsoncFile.Load` throws on a missing file, breaking the new helper | **REFUTED by measurement** — returns an empty object at `:19`. I tested my own suggested fix. |
| — | Dependency Cynic | Suspected the MCP package version could drift | **REFUTED** — pinned at exactly `1.4.0` in the csproj |

### Round 3 — folded findings

| # | Seat | Finding | Fold |
|---|---|---|---|
| 13 | Literal Implementer | ⛔ **The plan deleted `AgyConfigWriter` members BY LINE RANGE, and two ranges spanned LIVE code.** `DeploySkill()` ends at `:60` but the plan said `:36-80`, covering `RemoveSkill()` at `:68`; Install-overload-2 was cited `:105-127`, covering `Detail()` at `:127`. Both are called by the live `Uninstall()`. | Signature-matched deletion + a MUST-SURVIVE table + a verification grep. Task 6 got the same treatment though its range was correct. **The end-lines were the one class of citation I inferred instead of verifying.** |

### Round 4 — folded findings

| # | Seat | Finding | Fold |
|---|---|---|---|
| 14 | agy Boundary Smuggler | The structural sweep strips comments but **not string literals**, so a stray `var x = "ServerInstructions = ActivationPayload.Core";` satisfies the gate while the real wiring is deleted | Strip verbatim + regular string literals, **and** anchor the match on the `AddMcpServer(...)` call itself |
| 15 | agy Axiom Breaker | ⛔ **The split broke a pronoun's referent.** In recomposed `Text`, *"For those…"* landed after the fallback line, so *"those"* pointed at the read-only tools the core had just said need **no** lease — a flat contradiction | ⚠ **agy's proposed fix (reword the sentence) was WRONG — measured, it lands prose at 1132/1100 and breaks the asserted budget.** Fixed by REORDERING the addendum so the pointer leads it and sits adjacent to its lease line again: **1074/1100, zero cost.** |
| 16 | agy Literal Implementer | `private static SeedAgyInstall` in a shared class cannot be called from the two test classes — CS0122 | `internal static` |
| 17 | agy Literal Implementer | Task 8's ROADMAP cell replacement spans four lines; a literal newline inside a GFM table cell breaks the table | Collapsed to one line, with the reason stated |
| — | Resource Vampire | — | **no new findings** (stated plainly, not padded) |

### Round 5 — folded findings (narrowed round: only what a compiler and a passing suite cannot catch)

Seats were bespoke this round — the standard palette was exhausted after four rounds.

| # | Seat | Finding | Fold |
|---|---|---|---|
| 18 | **Adversary of the Reviewer** | ⛔ **Fold 14 — which this same reviewer proposed one round earlier — was BROKEN.** Stripping comments before strings lets one `"http://localhost/"` orphan a quote; the string sweep then runs away and swallows the `AddMcpServer` block. **The gate would fail on a perfectly valid file.** | **MEASURED all three orderings.** comments-then-strings: anchor destroyed by a URL. strings-then-comments: anchor destroyed by a `"` inside a comment. **comments-only: survives both AND still catches the commented-out mutant.** So fold 14's string-stripping is REVERTED; the `AddMcpServer` anchoring from the same fold is KEPT. agy proposed Roslyn — rejected as a dependency for one assertion. The accepted limit is documented in the test. |
| — | Fold Auditor | — | **no new findings** |

### Round 6 — folded findings

| # | Seat | Finding | Fold |
|---|---|---|---|
| 19 | **Adversary of the Reviewer** (own) | ⛔ **Fold 2's prune list drops `The_deployed_skill_is_the_embedded_seed`, which MEASURED is the only test anywhere checking the CONTENT of a shipped `SKILL.md`.** `SkillLoadLineTests` reads the REPO copies; `CliRouterPluginRegistrationTests` checks only that the staged file EXISTS. A truncated or empty extract would ship a skill with no frontmatter, every test would pass, and **agy's only activation channel would vanish silently** — defeating Part B via Part C. | New Task 6 Steps 2c/2d: assert the staged `SKILL.md` is byte-equal to the csproj's embedded source (`.claude/skills/...`), plus a mutant that breaks the embed and proves the gate red. |

⚠ **The lesson worth more than the fix:** the seat asked to name *"the fold most likely to be wrong"*
named **its own**, and it was right. No mechanical seat had found it across four rounds. A long panel
should always seat an adversary of the reviewer.
`Frontmatter(string)` is defined in Task 4 Step 1 beside the existing `Read(string)` it calls.
