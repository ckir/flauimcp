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

`scripts/release.ps1` handles the full release lifecycle. It computes semver from conventional commits since the last `v*` tag and runs the pre-flight gate (`dotnet build -c Release`, tests, 3-file version-sync, plugin-artifact drift). It drafts the CHANGELOG body via `claude -p` (the script owns the `## [X.Y.Z] - date` heading) and pauses for your review. Finally, it commits `chore(release): vX.Y.Z`, tags `vX.Y.Z`, and runs `git push --atomic origin master vX.Y.Z`.

```powershell
pwsh -File scripts/release.ps1 -WhatIf   # Preview gate and LLM prompt. No LLM calls, no writes.
pwsh -File scripts/release.ps1           # Cut the release.
```

| Flag | Description |
|---|---|
| `-Help`, `-H`, `-?` | Print usage and exit. |
| `-WhatIf` | Preview gate and prompt. No writes, never calls the LLM. |
| `-Yes`, `-y` | Unattended: auto-accept draft and confirmation. Interactive gates hard-fail instead of blocking. |
| `-Version X.Y.Z` | Pin the release to this exact version. |
| `-Bump major\|minor\|patch` | Force a bump level from the last tag. |
| `-Model <name>` | `claude -p` model used for the draft. Default: `haiku`. |

Pushing the `vX.Y.Z` tag triggers `.github/workflows/release.yml`. CI builds the executable and the Inno Setup installer, generates checksums, and publishes a GitHub Release using the top section of `CHANGELOG.md` as the body.
