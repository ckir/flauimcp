# Release tooling (D1 + item 12) — design

**Status:** design agreed 2026-08-20. Branch `fix/installer-smoke-gate`.
**Scope:** the first of four subprojects folded into v1.0.0. Two independent changes that share a theme —
tooling that claimed to work and did not.

---

## Why these two are one subproject

They are unrelated in code and identical in kind: **a mechanism that reports success while doing nothing,
or the opposite of what it says.** D1 tells the user their plugin will be restored and destroys it; item 12
prints `0 Warning(s)` from a gate that inspected nothing. Both were found the same way — by running
something for the first time.

They are batched because they are small, they both land in release tooling, and neither touches the other's
code. If either grows, split it; nothing here depends on them shipping together.

---

## D1 — the installer destroys a conflicting marketplace copy

### The defect, as filed versus as measured

The filed report (`docs/fix-the-tool-backlog/install-removes-conflicting-marketplace-copy-and-never-restores-it.md`)
blamed the disable/restore machinery. **That was wrong, and the correction is the whole design.**

MEASURED 2026-08-20 in isolated `CLAUDE_CONFIG_DIR` sandboxes:

| Step | Result |
|---|---|
| `claude plugin disable flaui-mcp@flaui-mcp --scope user` | exit 0, `✔ Successfully disabled`, still listed, `enabled: false` |
| the marker `disabled-plugins.json` after our install | correct: `{id: flaui-mcp@flaui-mcp, scope: user, projectPath: null}` |
| `installed_plugins.json` after our install | holds ONLY `flaui-mcp@flaui-mcp-marketplace` — the user's entry is GONE |
| `claude plugin uninstall flaui-mcp` (bare name) run against a disabled copy | exit 0, `✔ Successfully uninstalled`, list becomes EMPTY |

**Root cause: `ClaudePluginRegistrar.cs:33`.** An idempotency sweep runs
`claude plugin uninstall flaui-mcp` with the **bare plugin name**, no marketplace qualifier and no scope.
Both marketplaces install a plugin *named* `flaui-mcp`; only the qualified id distinguishes them. So the
sweep matches the user's copy and uninstalls it — moments after `ClaudeCollisionRemedy` carefully disabled
and recorded it.

⚠ **Both subsystems are individually correct.** `ClaudeCollisionRemedy` is careful code with panel history
in its comments; the registrar's sweep is a reasonable idempotency step. The defect exists only in their
sequence. This is why reading either file alone would not have found it, and why the fix is not "make the
remedy more robust".

### The fix, in two layers

**(a) Qualify the sweep — the root-cause fix.**

`ClaudePluginRegistrar.cs:33` and `:61` target `PluginIds.InstallTarget`
(`flaui-mcp@flaui-mcp-marketplace`) instead of `PluginIds.PluginName` (`flaui-mcp`). Our sweep can then
never match a copy we do not own.

Both constants already exist in `PluginArtifactWriter.cs:8-12` (`internal static class PluginIds`), so the
change is a constant swap at two call sites — no new plumbing:

```csharp
// ClaudePluginRegistrar.cs:33 and :61, currently:
_cli.Invoke(Claude, "plugin", "uninstall", PluginIds.PluginName);      // "flaui-mcp" — matches ANY marketplace
// becomes:
_cli.Invoke(Claude, "plugin", "uninstall", PluginIds.InstallTarget);   // "flaui-mcp@flaui-mcp-marketplace"
```

⚠ Both invocations are deliberately unchecked (`// swallow`) because an absent plugin is the normal case on
a first install. That stays true — a qualified id that is not installed exits non-zero exactly as the bare
name did, and the result is still ignored.

⚠ **The legacy-cleanup risk was raised and is CLOSED by measurement.** A bare-name sweep also cleans up a
copy registered under some *older* id. `git log -S` over `PluginArtifactWriter.cs` shows
`MarketplaceName = "flaui-mcp-marketplace"` is the **only value it has ever had** — introduced once at
`d9a464b`, never changed. There is no legacy id to lose. Had there been one, the fix would have been an
explicit allowlist of our own ids rather than a single qualified target.

**(b) Reinstall fallback — defence in depth.**

`ClaudeCollisionRemedy.Restore()` at `:218-222` already detects "recorded but no longer installed" and
warns. That branch becomes: **reinstall from the recorded marketplace, then re-enable.**

This is deliberately kept even though (a) removes the only known path that destroys the copy. It protects
against any other eviction — a future registrar change, a `claude` CLI behaviour shift, or the user's own
tooling. The operator chose both layers over either alone.

### The marker schema change, and the trap in it

`DisabledEntry` is `{Id, Scope, ProjectPath}`. Reinstalling needs the **originating marketplace**, so a
fourth field is required and the marker's schema version must rise.

⚠⚠ **A NAIVE VERSION BUMP SILENTLY BREAKS DOWNGRADES, and `CollisionMarker.cs:166` is why.**
`ReadState` returns `FutureVersion` for `version > 1`, and `Restore()` at `:156` treats that as *leave it
untouched and do not act on it*. So if v2 writes `"version": 2` and the user then runs an older build — a
downgrade, or a second machine — that build sees a future marker, **abandons it, and never re-enables the
user's plugin**. The FutureVersion guard is correct and must not be weakened; the schema change has to
avoid triggering it.

**Resolution: keep `"version": 1` and add the marketplace as an OPTIONAL field.** A v1 reader ignores an
unknown property (verify this against the actual parse at `:151-170` during implementation — the plan owns
that check). A new reader treats a missing marketplace as "not recorded", which degrades to today's
behaviour: re-enable only, no reinstall. Both directions stay safe, and the FutureVersion path stays
reserved for a genuinely incompatible change.

If implementation finds a v1 reader does NOT tolerate the extra field, the fallback is a version bump plus
teaching old builds nothing — and accepting the downgrade hole explicitly, in the ledger, rather than
silently. **The plan must not proceed on the assumption; it must measure it.**

### Behaviour contract

- **Install, no conflicting copy present:** unchanged. No marker, no warning.
- **Install, conflicting copy present:** it is disabled (not uninstalled) and recorded with its
  marketplace. The existing warning — *"they will be re-enabled if you uninstall flaui-mcp"* — becomes
  TRUE, which it currently is not.
- **Uninstall, copy still installed:** re-enabled as today.
- **Uninstall, copy missing:** reinstalled from the recorded marketplace, then re-enabled. If the reinstall
  fails, the user gets the exact command to run, as the existing failure paths already do.
- **Uninstall, marker has no marketplace (written by an older build):** re-enable only, warn as today. No
  guessing.

### Acceptance

`scripts/install-smoke.ps1`'s two collision checks — `marketplace copy is DISABLED after install` and
`marketplace copy is RESTORED after uninstall` — go GREEN. They are already written, already correct, and
currently failing. **They are the oracle; do not adjust them to fit the implementation.**

⚠ The reinstall path needs its own coverage, because the existing checks exercise it only if something
else evicts the copy — and (a) is meant to stop that happening. The plan owns adding a check that
explicitly removes the copy between install and uninstall, then asserts it comes back.

---

## Item 12 — the 0-warning gate enforces nothing

### The defect

`dotnet build FlaUI.Mcp.slnx -c Release` is INCREMENTAL and does not re-report warnings for projects it
considers up to date. MEASURED during SP4: it printed `0 Warning(s)` on a tree where `--no-incremental`
printed **2**. A warning introduced by one commit is therefore invisible to every later build gate — which
is how SP4's own `MaskEscalation.cs` shipped two CS8629 warnings through a gate that reported clean.

### The fix

**One new `Directory.Build.props` at the repository root**, setting `TreatWarningsAsErrors`. MSBuild applies
it to all three projects automatically; no `.csproj` edits.

The mechanism is the point: a warning becomes an **error**, so the project **fails to build**. MSBuild never
marks a failed project up to date, so the next incremental build recompiles it and reports the error again.
**The gate enforces itself without `--no-incremental` anywhere.**

⚠ **VERIFIED SAFE TO ENABLE: a clean `--no-incremental` build of the current tree is 0 Warning(s) /
0 Error(s).** There is no latent warning waiting to become a hard failure. This was measured, not assumed —
turning warnings into errors on a tree with unknown warning state would be reckless.

### Acceptance

A **mutant proof**, not a passing build: introduce a deliberate warning (an unused variable is enough),
confirm the build **FAILS**; revert, confirm it passes. A green build proves only that the property parses.

The existing gate commands in `docs/building.md` keep working unchanged. `--no-incremental` remains useful
for a belt-and-braces check but stops being load-bearing.

⚠ **This does not close ROADMAP item 13** (108 bare catches repo-wide). Different defect, still tracked,
explicitly out of scope here.

---

## Testing

| Change | How it is proven |
|---|---|
| (a) qualified sweep | `install-smoke.ps1` collision checks go green |
| (b) reinstall fallback | a NEW smoke check that removes the copy between install and uninstall |
| marker optional field | a v1 marker must still restore under new code — measured, not assumed |
| item 12 | mutant: a deliberate warning must FAIL the build |

The headless suite (894) and the Desktop suite (156 + PopupGrafting 1) must stay green. Neither change
touches the pixel path, so the Desktop suite is a regression check rather than the subject.

⚠ `install-smoke.ps1` is a MANUAL gate — it needs the real `claude` CLI and network access to seed a
marketplace, and it is not part of `dotnet test`. It must be run by hand and its output recorded.

---

## Out of scope, stated so it is not silently absorbed

- ROADMAP item 13 (bare catches repo-wide).
- Items 8, 4, 10 — the other three v1.0.0 subprojects, each with its own spec.
- The `agy` half of the installer. D1 is Claude-specific; nothing measured suggests the agy path has the
  same collision, and nothing here changes it.
- `SweepBackups` ignoring the `--agent` filter — a known wart noted in `install-smoke.ps1`'s own header,
  pre-existing and untouched.
