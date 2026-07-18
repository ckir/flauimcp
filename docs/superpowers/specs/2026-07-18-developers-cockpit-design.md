# DevelopersCockpit.ps1 — design (flaui-mcp)

**Goal:** a single-keypress interactive PowerShell menu at the repo root that fronts flaui-mcp's dev tasks.
Modeled on `clavity/DevelopersCockpit.ps1`. Thin. Delegates everything. Zero business logic of its own.

**Status:** owner-approved; ship-tier + gate decisions converged with agy (see §Decisions).

---

## Scope

A dev-box convenience launcher. Never shipped to an end user, so it freely assumes the dev toolchain.

**In:** an interactive tiered menu whose every row DELEGATES to the canonical task layer — `dotnet`,
`scripts/*.ps1`, `git`, `gh`, `pwsh`. Print each delegated command before running it. Surface a failure and
keep the menu alive.

**Out (non-goals):**
- No `-Action`/scriptable mode. `dotnet` + `scripts/*.ps1` ARE the scriptable CLI; the cockpit is the
  interactive front-end only.
- No version/bump/gate/release logic of its own. `release.ps1` owns all of that.
- No monorepo/member concepts (no `just`, no `lefthook`, no per-member bump) — flaui-mcp is one project.
- No unit tests for the menu itself (see §Testing).

---

## Architecture

One file, `DevelopersCockpit.ps1`, at the repo root. `#requires -Version 7`, `Set-StrictMode -Version Latest`,
`$ErrorActionPreference = 'Stop'`, `Set-Location $PSScriptRoot` (delegated commands resolve from the repo root
regardless of caller cwd).

**Pure-data action table (the review seam).** Each row is `@{ Key; Tier; Desc; Note; Cmd XOR Handler }`:
- `Cmd` — a literal command string run via the delegation helper.
- `Handler` — a scriptblock, for rows that prompt or need an owner-gate.
- Exactly one of `Cmd`/`Handler` per row. The table is the spec of behavior; reviewing it = eyeballing each
  Key → command.

**Delegation helper** `Invoke-Cmd([string]$Cmd)`: echo `> $Cmd` (dim), run it, `throw` on non-zero
`$LASTEXITCODE`. `$Cmd` is always a hardcoded literal from the table (never operator free-text).

**Render + loop:** clear (only when interactive), print the banner, print each tier and its rows. Read a key,
match a row, run its `Cmd`/`Handler` inside try/catch — any failure prints in red and the loop continues.
`Q` sets a quit flag. "Press Enter to continue" between actions.

**Presentation:** `NO_COLOR`- and non-TTY-aware (`[Console]::IsOutputRedirected`). A `Write-C` helper drops
color when redirected or `NO_COLOR` is set.

**Banner:** `DEVELOPERS COCKPIT — flaui-mcp   [v<version> | <branch> | last <tag>]`. All display-only, best-effort
(a missing source shows `?`, never aborts): version from `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`
`<Version>`, branch from `git rev-parse --abbrev-ref HEAD`, tag from `git describe --tags --match v* --abbrev=0`.

---

## Action set

### [1] INNER LOOP
| Key | Desc | Delegates to |
|---|---|---|
| B | Build (Debug) | `dotnet build FlaUI.Mcp.slnx -c Debug` |
| T | Test (unit) | `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput"` |
| R | Regen plugin snapshot | `pwsh -File scripts/build-plugin.ps1` |
| N | Scaffold new tool (`writes`) | Handler: prompt Name (validate `^[A-Z][A-Za-z0-9]+$`), then `pwsh -File scripts/new-tool.ps1 -Name <Name>` |

### [2] QUALITY GATE
| Key | Desc | Delegates to |
|---|---|---|
| G | Dev gate (build + test + Pester) | Handler: run Build, then Test, then Pester in sequence |
| E | Pester (scripts/) | `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/"` |
| I | Install smoke | `pwsh -File scripts/install-smoke.ps1` |

Pester MUST pin `-RequiredVersion 5.8.0` — this machine also has a legacy Pester 3.4.0, and the unpinned
module resolves ambiguously.

### [3] SHIP & RELEASE
| Key | Desc | Delegates to |
|---|---|---|
| V | Release preview | `pwsh -File scripts/release.ps1 -WhatIf` |
| C | Cut release (`writes`) | `pwsh -File scripts/release.ps1` |
| P | Push to origin (`OWNER`) | Owner-gate → `git push` (current branch to its upstream) |

`release.ps1` runs its OWN authoritative gate (build `-c Release` + test + version-sync + plugin-drift under
`-WhatIf`) and halts for its own typed confirmation before any write/tag/push. So the cockpit does **not** wrap
`V`/`C` in an extra owner-gate — it would be redundant. Only the raw `P` push (which `release.ps1` structurally
does not offer — its only push is the atomic commit+tag+push of a full release) carries the cockpit's own
owner-gate.

### [4] HOUSEKEEPING
| Key | Desc | Delegates to |
|---|---|---|
| H | Health check | probe `pwsh`, `dotnet`, `git`, `gh`, `claude`, `pester` (5.8.0), `iscc` (Inno Setup) |
| D | Bundle codebase | `pwsh -File BundleCodeBase.ps1` |
| A | Reset GH Actions storage (`OWNER`) | Owner-gate → `pwsh -File GitHub_Actions_Storage_Reset.ps1` |
| Q | Quit | Handler: set quit flag |

---

## Owner-gate

`Confirm-Owner([string[]]$Commands)`: print `OWNER-GATED — this will run:` then each command, then require the
operator type the literal case-sensitive word `push` (`-cne 'push'` aborts). Applied to exactly two actions:
`P` (push to origin) and `A` (reset GH Actions storage) — the outward/destructive ones. Everything else runs on
a single keypress.

## Health check

Probe each tool independently; one missing tool never stops the sweep. For each: if the exe isn't on `PATH`
(`Get-Command`), print `[FAIL] <name> (not on PATH)`; else run its version probe and print `[ ok ] <name>
<version>`. Probes: `pwsh --version`, `dotnet --version`, `git --version`, `gh --version`, `claude --version`
(release drafting), `iscc` (presence only — no `--version`; may be absent on a dev box), and Pester 5.8.0
availability (`Get-Module -ListAvailable Pester` includes 5.8.0). `claude` and `iscc` absence is a soft warning,
not a hard fail — they're only needed for release/installer paths.

## Error handling

Every action runs inside the loop's try/catch. `Invoke-Cmd` throws on non-zero exit; a handler may throw on bad
input. The catch prints `[cockpit] Error: <message>` in red and returns to the menu. Nothing a single keypress
does is irreversible except via the two owner-gated actions and `C`/`P` (which are self-gated / owner-gated).

## Testing

The menu is an interactive delegating shell — no unit tests (matches the clavity model). Correctness is
review-time: eyeball the pure-data action table (each Key → a real, existing command) and, once, run each row
by hand. The delegated commands (`dotnet`, `scripts/*.ps1`, `release.ps1`) carry their own tests.

---

## Decisions (converged with agy)

1. **Ship tier = pure delegation.** `release.ps1` is flaui-mcp's canonical release engine (semver math, gate,
   claude-p drafting, safety confirms, half-finished recovery). The cockpit only previews (`-WhatIf`) and cuts
   (`release.ps1`), trusting the script's own gates. No reimplemented bump/tag/push.
2. **Keep the raw `P` push.** `release.ps1` only pushes as the atomic final step of a full release; it has no
   "sync my committed work to origin without releasing" path — a distinct, common dev need. So a single
   owner-gated raw `git push` is an orthogonal action, not reimplemented release logic.
3. **`G` stays fast (build + test + Pester).** The heavy authoritative gate already runs via `V`
   (`release.ps1 -WhatIf`). `install-smoke` is a different axis (installer/packaging health, not code
   correctness), so it stays a standalone `I` action — folding it into `G` would kill the fast-feedback loop.
