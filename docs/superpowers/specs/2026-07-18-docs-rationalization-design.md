# flaui-mcp documentation rationalization — design

**Date:** 2026-07-18
**Status:** approved (design) — implementation by agy, reviewed by Claude
**Problem:** `README.md` (333 lines) grew into a wall of text unusable to humans, and it overlaps an
existing `docs/` set (e.g. "What it does" appears in both README and `features-and-safeguards.md`). Split
the corpus into a small set of role-scoped documents, demote the README to a lean router, and rewrite the
human-facing docs in a terse, human voice — not the dense agent-oriented prose they carry now.

## Decisions (settled — do not re-litigate)
- **Structure = the Daemon Split** (chosen over a Diátaxis split). Organize by *who reads it*, because for an
  MCP daemon that boundary is also a *style* boundary: human-operator prose vs. dense agent contract. (agy
  raised this via a Unix man-page analogy; Claude agreed; user approved.)
- **Voice = terse technical** for the human-facing docs (README, operator-manual, architecture-and-safety).
- **Writer = agy. Reviewer = Claude.** agy drafts each doc to this spec + the voice guide; Claude reviews
  each against migration completeness, role purity, and voice. The user holds the final call.

## Target document map
| File | Audience / role | Voice |
|------|-----------------|-------|
| `README.md` (≤ ~90 lines) | Router: orient + get running fast, then leave | terse-technical |
| `docs/operator-manual.md` | The human who installs, configures, runs, and audits the daemon | terse-technical |
| `docs/agent-contract.md` | The RPC surface an agent (or a human debugging one) consumes | terse, schema-dense |
| `docs/architecture-and-safety.md` | The guarantees and rationale — how it works and why it's safe | terse-technical |
| **Kept as-is:** `docs/building.md`, `ROADMAP.md`, `CONTRIBUTING.md`, `CLA.md`, `CHANGELOG.md` | — | — |

`docs/features-and-safeguards.md` and `docs/ops-manual.md` are **retired** — their content migrates into the
new docs (table below), then the two files are deleted. All inbound links are updated.

## Content migration — every current section has exactly one home
| Current location | Destination |
|------------------|-------------|
| README: title/badges, maintainers, license, contributing-link | `README.md` (kept, trimmed) |
| README: WARNING/DISCLAIMER (~40 lines) | `README.md` = 5-line loud warning; full rationale → `architecture-and-safety.md` |
| README: What it does | `README.md` = 2–3 line pitch; capability overview → `architecture-and-safety.md` |
| README: Targeting (`ref`/`selector`) | `agent-contract.md` |
| README: Documentation (link map) | `README.md` (regenerated for the new 3-doc set) |
| README: Requirements | `README.md` = brief bullet; full → `operator-manual.md` |
| README: Installation (standalone / silent / Claude Code / agy parity) | `README.md` = ONE quickstart path; all options → `operator-manual.md` |
| README: Usage | `operator-manual.md` (its first-drive example seeds the README quickstart) |
| README: Watching & auditing the agent (+ "Known limitations (auditing)") | how-to steps → `operator-manual.md`; the model/guarantees → `architecture-and-safety.md` |
| README: Known limitations | `agent-contract.md` |
| `features-and-safeguards.md`: What it does | `architecture-and-safety.md` |
| `features-and-safeguards.md`: Synthetic input · Read-only mode · Perception safeguards · User-state presence | `architecture-and-safety.md` (rationale); the operator-set flags → `operator-manual.md` (CLI) |
| `features-and-safeguards.md`: Event streaming (`desktop_watch`) · Opaque apps (wake + find_text) · Electron/Chromium | `agent-contract.md` (the tools); the "why opaque" concept → `architecture-and-safety.md` |
| `features-and-safeguards.md`: How it compares to WebDriver/Appium | `architecture-and-safety.md` |
| `ops-manual.md`: manual install · what the installer changes · uninstall · CLI reference | `operator-manual.md` |
| `building.md` · `ROADMAP.md` · `CONTRIBUTING.md` · `CLA.md` · `CHANGELOG.md` | keep as-is |

**Boundary rule (this is what kills duplication):** a tool's *schema* lives in `agent-contract.md`; a safety
*rationale* lives in `architecture-and-safety.md`; a flag an operator *sets* lives in `operator-manual.md`.
Each fact has exactly one canonical home; every other mention links to it instead of repeating it.

## Per-document outline (order of sections)
### README.md — the router (≤ ~90 lines)
1. Title + one-line description.
2. Loud safety warning: 5 lines max — "this drives real Windows input; it can do damage; input is locked by
   default." Link to `architecture-and-safety.md` for the full model.
3. What it is: 2–3 lines.
4. Quickstart: ONE install path (standalone installer) + register with Claude Code + one first command that
   produces visible output. Nothing else.
5. Documentation map: four links (operator-manual, agent-contract, architecture-and-safety, building) with a
   one-line "read this when…" each.
6. Requirements: 3–4 bullets; link to operator-manual for detail.
7. Maintainers · Contributing (link) · License.

### docs/operator-manual.md — for the human running it
Requirements (full) → Install (all options: standalone, silent one-liner, manual) → Register with Claude Code
→ agy (Antigravity) parity → CLI reference (`flaui-mcp` commands + all flags, incl. `--read-only-mode`,
`--allow-shells`, `unlock`) → Grant/scope a lease → Run in read-only mode → Watch & audit the agent (steps)
→ What the installer changes → Uninstall.

### docs/agent-contract.md — the RPC surface
Targeting (`ref` vs `selector`) → the `desktop_*` tool catalog (grouped: perception / interaction / input /
events / opaque-app access) → event streaming (`desktop_watch`) → opaque-app access tools (wake, find_text)
→ known tool limitations.

### docs/architecture-and-safety.md — guarantees and rationale
What it does (capability concept) → the dual-axis safety model (lease axis vs destructive axis) → why
synthetic input needs a lease → read-only mode rationale → perception safeguards → user-state presence →
the auditing model and what it guarantees → why opaque apps need waking → how it compares to WebDriver/Appium
→ the full danger/disclaimer rationale.

## Voice guide
Applies to the human-facing docs (`README`, `operator-manual`, `architecture-and-safety`):
- Short declarative sentences, one idea each. Active voice. Imperative for steps ("Run…", "Grant a lease…").
- Cut throat-clearing and hedging: no "it's worth noting", "simply", "please note", "In this section we…",
  "As mentioned". Delete any sentence that only restates its heading.
- Show the command and its real output; don't describe them in prose.
- Scannable: headings, tight fenced code blocks, small tables. Bullet lists ≤ 5 items; use prose for
  narrative, not stacked fragments.
- No AI-tells: no "Let's…", no summary-of-a-summary, no hedged em-dash chains.

`agent-contract.md` follows the same terseness but is allowed to be schema-dense and long — it is a lookup
surface for agents, so exactness and completeness beat narrative flow.

## Success criteria (checkable)
1. A new evaluator answers "what is this, is it safe, how do I install it" from the README alone in < 60s.
2. Any reader task ("audit the agent", "run read-only", "look up a flag", "why does typing need a lease")
   lands on exactly ONE owning doc, ≤ 2 clicks from the README.
3. No human-facing doc exceeds ~250 lines. `agent-contract.md` may exceed this (lookup surface).
4. No fact appears in two docs. Grep spot-checks: "What it does", the disclaimer text, and the flag
   descriptions each appear in exactly one file.
5. Every current section in the migration table is present in its destination; `features-and-safeguards.md`
   and `ops-manual.md` are deleted with no dangling inbound links.

## Constraints
- Never rename the literal flags `--read-only-mode` and `--allow-shells`.
- Preserve the two-axis safety model facts exactly: lease axis (only SendInput tools — type/click/key/drag/
  paste/set_value — need `flaui-mcp unlock`) vs destructive axis (state-changing tools blocked in
  `--read-only-mode`; some lease-exempt tools are still destructive). Do not "simplify" these into one axis.
- This is a documentation move + rewrite. No code, no tool behavior, no CLI changes.
- Land on a feature branch off `master`; do not push/merge/tag without explicit user approval.

## Non-goals / follow-ons
- **`CONTRIBUTING.md` audit is a SEPARATE follow-on** (user-requested, after this work). Out of scope here;
  `CONTRIBUTING.md` is kept as-is by this spec and audited later.
- Not rewriting `ROADMAP.md`, `CLA.md`, `CHANGELOG.md`, or `building.md` content.

## Implementation & review model
1. One agy task per new doc (`operator-manual`, `agent-contract`, `architecture-and-safety`), then the README
   router, then the link/cleanup pass (delete the two retired docs, fix inbound links). Each task cites this
   spec, its per-doc outline, its migration rows, and the voice guide.
2. Claude reviews each drafted doc against: (a) migration completeness — every mapped section present, nothing
   orphaned; (b) role purity — the doc stays in its lane, boundary rule honored, no cross-doc duplication;
   (c) voice — terse-technical, no AI-tells.
3. The user reviews before anything merges.
