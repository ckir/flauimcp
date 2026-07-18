## What & why

<!-- One or two sentences: what does this change and why? -->

## Checklist

- [ ] I have signed the CLA (see [CLA.md](../CLA.md)) — automated bot enforcement isn't live yet, so the maintainer verifies this manually.
- [ ] Headless tests pass locally: `dotnet test --filter "Category!=Desktop&Category!=KnownDefect"`.
- [ ] Desktop tests run locally on an unlocked session — **or N/A** (no UIA-backed behavior).
- [ ] Build is clean (`dotnet build -c Release`, no new warnings).
- [ ] **Safety annotation is correct** — safe reads are `[McpServerTool(ReadOnly = true)]`, state-changing tools are `Destructive = true` (a mis-annotation bypasses `--read-only-mode`).
- [ ] `docs/agent-contract.md` tool catalog + `CHANGELOG.md [Unreleased]` updated (if this adds/changes a tool).
- [ ] Follows the tool pattern: auto-discovered, thin Server method, logic in Core, error envelope.
