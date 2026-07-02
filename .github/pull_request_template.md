## What & why

<!-- One or two sentences: what does this change and why? -->

## Checklist

- [ ] I have signed the CLA (the bot will prompt on my first PR).
- [ ] Headless tests pass locally: `dotnet test --filter "Category!=Desktop"`.
- [ ] Desktop tests run locally on an unlocked session — **or N/A** (no UIA-backed behavior).
- [ ] Build is clean (`dotnet build -c Release`, no new warnings); ran `dotnet format` if an `.editorconfig` is present.
- [ ] **Safety annotation is correct** — safe reads are `[McpServerTool(ReadOnly = true)]`, state-changing tools are `Destructive = true` (a mis-annotation bypasses `--read-only-mode`).
- [ ] README tool table + `CHANGELOG.md [Unreleased]` updated (if this adds/changes a tool).
- [ ] Follows the tool pattern: auto-discovered, thin Server method, logic in Core, error envelope.
