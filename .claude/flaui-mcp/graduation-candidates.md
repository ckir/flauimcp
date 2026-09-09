# flaui-autotrain graduation candidates

GROWTH rules that have earned a place in the hand-authored body of `driving-flaui-mcp/SKILL.md`.
Appended by `flaui-curate` when the GROWTH region hits its line cap; a maintainer folds each into
the manual proper (above the AUTOTRAIN:GROWTH markers) and deletes the line here. Loaded by nobody at runtime.

## Candidates
- Common file dialog ("Save as"/"Open"): the filename box is buried under ~20+ `System.ItemNameDisplay` file-list cells — target it by name `"File name:"` / automationId `1001`; recipe: `set_value` full path → click Save (automationId `"1"`). Modal file dialogs ARE enumerable top-level windows.
- Rename a file (F2) opens a SEPARATE top-level window (title=filename) hosting the edit — `set_value` its `System.ItemNameDisplay` "Name" Edit → `Enter`. `explorer.exe <folderpath>` spawns a real new window+pid; launching Notepad instead TABS into the existing process (LaunchTimeout, but a same-pid tab-window appears).
- Explorer address bar reads empty via `get_text` in breadcrumb mode — read the path from the breadcrumb SplitButton segments (lease-exempt), or `Ctrl+L` to morph it to an editable Edit that returns the full path (`Esc` restores).
- `desktop_clipboard_get` returns `""` for BOTH empty AND non-text (file/image) clipboards (no format signal); `desktop_paste_text` refuses `ClipboardHoldsNonText` (`forceOverwriteClipboard` to override).
