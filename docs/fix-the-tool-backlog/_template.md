# <slug> — <one-line defect title>

- **Captured:** <YYYY-MM-DD> (via flaui-autotrain)
- **Regression test:** <FullyQualifiedName of the generated test, or "none — see Test-gen note">
- **Trait:** `Category=KnownDefect` (headless) | `Category=Desktop` (console-only)

## Steps to Reproduce
<The exact desktop_* call sequence + arrange that exhibits the quirk. REQUIRED to route here.>

## Code-level Mitigation
<The specific change to the C# execution path that removes it. REQUIRED to route here — if you
cannot state this, it is NOT tool-fixable and belongs in the driving skill as a heuristic, not here.>

## Test-gen note (only if no runnable test was generated)
<Why a Tier-2 partial repro could not be expressed as a desktop_* test call. Rare by construction.>
