# justfile - the second of this repository's two release routes.
#
# The first is DevelopersCockpit.ps1, an interactive menu. This file is the non-interactive twin: every
# recipe below is the SAME command string the cockpit runs for the same action, so the two routes cannot
# tell you different things about the same repository.
#
# What is deliberately NOT here: the cockpit's owner-gated and interactive actions - the Desktop suite
# (needs a physical console and an input lease), push-to-origin, the health check, and the Actions storage
# reset. Those prompt, and a recipe that skipped their gates would be a downgrade rather than a
# convenience. Use the cockpit for those.
#
#   just            list the recipes
#   just gate       what to run before you commit
#   just release    cut a release

set shell := ["pwsh", "-NoProfile", "-Command"]
set windows-shell := ["pwsh", "-NoProfile", "-Command"]

# List the recipes.
default:
    @just --list

# --- [1] INNER LOOP ---------------------------------------------------------

# Build the solution (Debug). There is no .sln - the solution is FlaUI.Mcp.slnx.
build:
    dotnet build FlaUI.Mcp.slnx -c Debug

# The headless suite. Never pass --no-build: a deleted test still runs from a stale DLL.
test:
    dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"

# Regenerate the plugin snapshot under plugins/flaui-mcp.
plugin:
    pwsh -File scripts/build-plugin.ps1

# Scaffold a new tool: just new-tool DesktopFoo
new-tool NAME:
    pwsh -File scripts/new-tool.ps1 -Name {{NAME}}

# --- [2] QUALITY GATE -------------------------------------------------------

# Run.Exit on a configuration object, NOT -CI: -CI also turns on TestResult.Enabled and drops
# testResults.xml into the working tree on every run. Pester 6 removed -EnableExit, which is what this
# used before, and a passing suite under it exits 1 - measured.
#
# The version below is pinned and guarded: scripts/pester-pin.Tests.ps1 scans this file and fails if it
# drifts from the version CI installs.
#
# The PowerShell suite under scripts/.
pester:
    Import-Module Pester -RequiredVersion 6.1.0; $c = New-PesterConfiguration; $c.Run.Path = 'scripts/'; $c.Run.Exit = $true; Invoke-Pester -Configuration $c

# Build, headless tests, and the PowerShell suite. The cockpit's [G] Dev gate.
gate: build test pester

# Needs an interactive session; it is not a CI job and never will be. The script header carries the
# scheduled-task registration one-liner.
#
# Run the LEASE-EXEMPT half of the Desktop (UIA) suite; dated report under artifacts/desktop-runs.
desktop:
    pwsh -File scripts/desktop-suite.ps1

# Install the git hooks (commit-msg subject check, pre-push headless gate). Once per clone.
hooks:
    lefthook install

# End-to-end install/uninstall smoke.
smoke:
    pwsh -File scripts/install-smoke.ps1

# --- [3] SHIP AND RELEASE ---------------------------------------------------

# Compute the next version and print the prompt that WOULD be sent, without writing anything.
release-preview:
    pwsh -File scripts/release.ps1 -WhatIf

# To stamp locally without publishing, use `just release-local` instead.
#
# Cut a release: gate, changelog draft, version bump, commit, tag, push.
release:
    pwsh -File scripts/release.ps1

# Stamp a release locally - commit and tag, but do not push.
release-local:
    pwsh -File scripts/release.ps1 -NoPush

# --- [4] HOUSEKEEPING -------------------------------------------------------

# Bundle the codebase for review.
bundle:
    pwsh -File BundleCodeBase.ps1
