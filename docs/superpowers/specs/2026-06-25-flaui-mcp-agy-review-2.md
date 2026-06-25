# FlaUI.Mcp — Design Review & Gap Analysis

**Review Date:** 2026-06-25
**Reviewer:** AI Agent Architect

This review examines the `FlaUI.Mcp` design specification through two lenses: Exhaustiveness (identifying structural holes, missing patterns, and UIA footguns) and Creativity (desktop-native capabilities that push beyond a standard Playwright clone).

---

## Part 1: Exhaustiveness (Gaps, Holes, & Footguns)

### 1. The `InvokePattern` Deadlock
* **Severity / Impact:** CRITICAL. Can hang the entire MCP server indefinitely.
* **What:** UIA `InvokePattern.Invoke()` is a blocking COM call. 
* **Why:** If the target application handles a button click by opening a modal dialog (e.g., "Save As", "Confirm") *on its UI thread*, `Invoke()` will not return until that modal is dismissed. Because the spec mandates a **single dedicated STA thread** (`AutomationDispatcher`), this will completely freeze the server. All subsequent agent commands (even checking `desktop_list_windows`) will queue up behind the blocked thread.
* **Concrete Suggestion:** Never trust `InvokePattern` on the main STA thread without a timeout or task-wrapping mechanism that can abandon the wait. Alternatively, heavily prefer the synthetic click path for standard buttons, or expose an `async: true` flag for `desktop_invoke` that fires it off via a background MTA thread wrapper (accepting the COM risk) to avoid blocking the primary STA dispatcher.

### 2. The Context Menu / Popup Blindspot
* **Severity / Impact:** HIGH. Agents will fail basic tasks like "right-click and copy."
* **What:** Context menus and dropdown popups in Windows often do not belong to the target Window's UIA tree.
* **Why:** A right-click in `w1` might spawn a Win32 context menu (`#32768` class) or a WPF `PopupRoot`. These exist as children of the **Desktop**, not `w1`. The agent will call `desktop_click(w1, e23, right)`, then call `desktop_snapshot(w1)`, and the menu items simply won't be there. The agent will hit a wall.
* **Concrete Suggestion:** The `SnapshotEngine` must be context-menu aware. When returning a snapshot for `w1`, it should query the Desktop for foreground popups/menus belonging to the same process, and dynamically graft them into the returned text tree under a `[Popups]` node.

### 3. Missing Crucial UIA Patterns (Grid, Text, Window)
* **Severity / Impact:** HIGH. Agents will be unable to read spreadsheets, scrape docs, or manage workspace visibility.
* **What:** The tool catalog omits `GridPattern`/`TablePattern`, `TextPattern`, and `WindowPattern`.
* **Why:** * *Grid/Table:* A `desktop_snapshot` of an Excel sheet or data grid will either truncate or explode the context limit. Agents need to query `desktop_get_cell(w1, ref, row, col)`.
    * *Text:* Getting the currently selected text or caret position in Notepad/VSCode requires `TextPattern`.
    * *Window:* Agents cannot move windows out of the way, maximize them, or restore them.
* **Concrete Suggestion:** 1. Add `desktop_window_transform {window, action: maximize|minimize|restore, bounds?}` (using `WindowPattern`/`TransformPattern`).
    2. Add `desktop_get_text_selection` and `desktop_get_grid_cell`.

### 4. Virtualized Items & Ref Resolution
* **Severity / Impact:** MEDIUM. Agent confusion in lists.
* **What:** `desktop_scroll_into_view` relies on `ScrollItemPattern`, but fails on complex UI virtualization.
* **Why:** In WPF/UWP, items off-screen don't exist in the UIA tree *at all*. A snapshot won't show them. If an agent knows "Item 50" exists but it's virtualized, it can't target it with an `e#` ref. 
* **Concrete Suggestion:** Expose `desktop_scroll` `{window, ref, direction, amount}` using the `ScrollPattern` on the *container* (e.g., the ListBox), allowing the agent to blindly scroll down to force realization, then re-snapshot.

### 5. The "Fast Path" Clipboard Omission
* **Severity / Impact:** MEDIUM. Waste of time and tokens.
* **What:** No clipboard integration.
* **Why:** `desktop_type` for large blocks of text (e.g., pasting a 20-line code snippet or a URL) is agonizingly slow via synthetic input and prone to UI focus interruption. 
* **Concrete Suggestion:** Add `desktop_clipboard_get` and `desktop_clipboard_set`. Let the agent set the clipboard, then hit `Ctrl+V` via `desktop_key`. 

---

## Part 2: Creativity (Desktop-Native Enhancements)

### 1. Semantic "What Changed" Diffs (Snapshot Delta)
* **YAGNI Risk:** Medium (Complexity), High (Value).
* **What:** A tool `desktop_snapshot_diff {window, sinceSnapshotId}`.
* **Why:** Sending a 4,000-token UI tree every time an agent clicks a checkbox is brutal for context limits. A diffing engine could return *only* the nodes whose UIA properties changed (e.g., `ToggleState`, `Name`, new/removed nodes).
* **Concrete Suggestion:** Keep a fast-hash of the previous UIA tree snapshot in the `SessionManager`. When the agent requests a diff, return: `[Modified: e45 CheckBox "Accept" {checked}], [Added: e99 Text "Success!"]`.

### 2. Native OCR Integration for "Black Box" Apps
* **YAGNI Risk:** Low (if using built-in Windows APIs), High (if bundling Tesseract).
* **What:** A fallback perception mode for games, Flash, Citrix, or heavily custom-drawn apps that have zero UIA presence.
* **Why:** If the agent opens a target and the snapshot is empty, it relies purely on the model's raw vision. Providing local, deterministic text bounding boxes drastically improves click accuracy on un-automatable UI.
* **Concrete Suggestion:** Leverage the `Windows.Media.Ocr` API (built into Windows 10/11 natively, callable from C#) to augment `desktop_screenshot`. Return a secondary overlay array: `[{text: "Login", bounds: [x,y,w,h]}]`. 

### 3. UIA Event Streaming (The "Watch" Pattern)
* **YAGNI Risk:** High for v1, but conceptually game-changing.
* **What:** Moving from polling to push. 
* **Why:** Agents currently have to loop: `Wait -> Snapshot -> Evaluate`. By subscribing to UIA events (`Window_Opened`, `StructureChanged`, `FocusChanged`), the MCP server could push SSE events directly to the LLM agent (if the client SDK supports async notification). 
* **Concrete Suggestion:** Add `desktop_watch {window, event: "Focus" | "WindowOpen"}`. The server queues an MCP notification when it fires. (Keep deferred for v2, as it requires bidirectional agent prompting).

### 4. Global Action Context (The Desktop is not a Browser)
* **YAGNI Risk:** Low.
* **What:** Playwright operates purely in isolated tabs. Windows operates in an overlapping Z-order ecosystem.
* **Why:** A notification toast from Teams can intercept a click meant for Notepad. A background app can steal focus. The agent has no concept of "What else is on the screen?" if it's explicitly locked to `desktop_snapshot(w1)`.
* **Concrete Suggestion:** Add a `desktop_get_foreground_state` tool that explicitly warns the agent if the targeted `w1` handle is partially occluded, or add a `desktop_snapshot_global` that provides a macro-view of the desktop Z-order (just titles and bounds, not deep UI trees) so the agent understands the true state of the operating system.
