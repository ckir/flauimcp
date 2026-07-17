# click-missing-modifiers-param — desktop_click description promises modifiers but schema omits them

- **Captured:** 2026-07-17 (via flaui-autotrain)
- **Regression test:** `FlaUI.Mcp.Tests.Server.ClickModifiersSchemaTests.DesktopClick_schema_has_no_modifiers_parameter_despite_its_description_promising_one`
- **Trait:** `Category=KnownDefect` (headless)

## Steps to Reproduce
1. `desktop_click`'s tool DESCRIPTION (`src/FlaUI.Mcp.Server/Tools/InputTools.cs:404`) reads: "...
   button=left|right|middle, count=1|2, modifiers optional. ...".
2. The tool's actual C# parameter list (`InputTools.DesktopClick`, same file, lines 405-411) is
   `window, ref, selector, button, count, timeoutMs` — there is NO `modifiers` parameter anywhere in the
   method signature, so the MCP input schema generated from it exposes none either.
3. Consequence: Ctrl+click / Shift+click is impossible via `desktop_click`. Combined with `desktop_select`
   being single-select-only (replaces the current selection, no additive/range mode), non-contiguous or
   range multi-selection is impossible through any UIA/click tool — only raw keyboard
   (`desktop_key Shift+Down`/`Ctrl+Space`) can additively select, and that path needs the input lease.

## Code-level Mitigation
Add an optional `modifiers` parameter (e.g. `string[]? modifiers = null`, same `Ctrl|Shift|Alt|Win` token
vocabulary `KeyChordParser` already uses for `desktop_key`) to `InputTools.DesktopClick`
(`src/FlaUI.Mcp.Server/Tools/InputTools.cs:404-411`) and thread it into the `_guard.MouseClick(...)` call
(currently hardcoded to `System.Array.Empty<string>()` for modifiers at the click site around line 465) so
the modifiers are actually held during the synthetic click. Either implement the description's promise or
correct the description to match the schema — but implementing it is the higher-value fix since it restores
additive/range multi-select via Ctrl/Shift+click.
