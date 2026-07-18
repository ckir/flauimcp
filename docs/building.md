[← Back to FlaUI.Mcp README](../README.md)

# Building from source

Requires the **.NET 10 SDK** (the project targets `net10.0-windows`).

```powershell
# Build + run tests. The Desktop-category tests need an interactive desktop; over RDP they run while the
# session stays connected and unlocked. SendInput works over connected RDP too (session-state, not RDP —
# no physical console required); it fails only when the session is disconnected/locked. See CONTRIBUTING.md.
dotnet build test/FlaUI.Mcp.TestApp
dotnet test

# Run only the non-desktop unit tests (e.g. in headless CI)
dotnet test --filter "Category!=Desktop"

# Produce the self-contained single-file exe
dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Cutting a release

`scripts/release.ps1` is the one-command release tool. It computes the next semver from
conventional commits since the last `v*` tag, then gates the release (`dotnet build -c Release`,
the unit test suite, a 3-file version-sync check, and a plugin-artifact drift check), drafts the
CHANGELOG body with `claude -p` (the script owns the `## [X.Y.Z] - date` heading), lets you review
the draft, then commits `chore(release): vX.Y.Z`, tags `vX.Y.Z`, and runs
`git push --atomic origin master vX.Y.Z`.

```powershell
pwsh -File scripts/release.ps1 -WhatIf   # preview: runs the gate, prints the LLM prompt; no writes or push
pwsh -File scripts/release.ps1           # cut the release
```

Flags:

| Flag | What it does |
|---|---|
| `-Help`, `-H`, `-?` | Print usage and exit. No side effects. |
| `-WhatIf` | Preview only: runs the gate and prints the changelog-draft prompt; makes no writes, never calls the LLM. |
| `-Yes`, `-y` | Unattended: auto-accepts the changelog draft and the final confirmation; other interactive gates hard-fail instead of blocking. |
| `-Version X.Y.Z` | Pin the release to this exact version instead of computing one. |
| `-Bump major\|minor\|patch` | Force a bump level from the last tag instead of computing it from commits. |
| `-Model <name>` | The `claude -p` model used for the changelog draft. Default: `haiku`. |

Pushing the `vX.Y.Z` tag triggers the release workflow
([`.github/workflows/release.yml`](../.github/workflows/release.yml)), which builds the exe and
the Inno Setup installer and publishes them — with checksums — to a GitHub Release, sourcing the
release body from the top section of `CHANGELOG.md`.
