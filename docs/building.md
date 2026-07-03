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

Tagging a commit `v*` triggers the release workflow
([`.github/workflows/release.yml`](../.github/workflows/release.yml)), which builds the exe and
the Inno Setup installer and publishes them — with checksums — to a GitHub Release.
