# Undocumented Features Audit

**Date:** 2026-07-18
**Target:** `docs/agent-contract.md` vs `src/FlaUI.Mcp.Server/Tools/*.cs` (expanded to CLI and Environment Variables)

## 1. Executive Summary

A comprehensive audit of the `[McpServerTool]` surface against `agent-contract.md` reveals a significant gap in parameter documentation. While the *tool names* and *high-level descriptions* are well-covered in the agent contract, the vast majority of optional parameters, configuration dials, and advanced targeting modes are completely missing from the documentation. 

This forces agents to discover parameters via MCP schema reflection rather than the contract document, leading to under-utilization of advanced features like cross-window dragging, offline snapshot stats, and explicit timeout control.

The audit has been expanded to cover missing CLI verbs and environment variables that are present in the codebase but absent from operator-facing documentation.

## 2. Category 1: Tool Parameters

| Feature | Code location | What it does | Suggested doc home |
|---------|---------------|--------------|--------------------|
| `timeoutMs` | `src/FlaUI.Mcp.Server/Tools/*.cs` | Explicit timeout control for perception and interaction tools. | agent-contract.md |
| `desktop_key` (`ref`/`selector`) | `src/FlaUI.Mcp.Server/Tools/InputTools.cs:Key` | Optionally focuses an element before sending the keystroke. | agent-contract.md |
| `desktop_snapshot` (`root`, `maxDepth`, `interactiveOnly`, `fullProperties`, `includeOffscreen`) | `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:Snapshot` | Tuning dials for the snapshot tree walk and scope. | agent-contract.md |
| `desktop_snapshot_diff` (`scope`) | `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:Diff` | Allows diffing only a specific subtree against a baseline. | agent-contract.md |
| `desktop_snapshot_stats` (`snapshotId`) | `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:Stats` | Supports passing an offline snapshotId instead of a live window. | agent-contract.md |
| `desktop_wait_for` (conditions) | `src/FlaUI.Mcp.Server/Tools/WaitTools.cs:WaitFor` | Polls a window until complex selector conditions hold. | agent-contract.md |
| `desktop_wait_for_stable` (conditions) | `src/FlaUI.Mcp.Server/Tools/WaitTools.cs:WaitForStable` | Polls until a tree stops structurally changing. | agent-contract.md |
| `desktop_get_text` (`selectionOnly`, `maxLength`, `fromEnd`) | `src/FlaUI.Mcp.Server/Tools/ContentTools.cs:GetText` | Fine-grained text pattern reading and slicing. | agent-contract.md |
| `desktop_read_terminal_tab` (`tabIndex`, `restoreFocus`, `fromEnd`, `maxLength`) | `src/FlaUI.Mcp.Server/Tools/ContentTools.cs:ReadTerminalTab` | Background reading of a specific terminal tab and focus restoration. | agent-contract.md |
| `desktop_screenshot` (`window`, `ref`, `maxWidth`) | `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:Screenshot` | Targeted snapshot capturing with a built-in downscaler. | agent-contract.md |
| `desktop_type` (`interKeyDelayMs`, `verify`) | `src/FlaUI.Mcp.Server/Tools/InputTools.cs:Type` | Pacing for reactive editors and advisory read-back checking. | agent-contract.md |
| `desktop_paste_text` (`verify`, `forceOverwriteClipboard`) | `src/FlaUI.Mcp.Server/Tools/InputTools.cs:PasteText` | Advisory read-back and option to clobber non-text clipboard contents. | agent-contract.md |
| `desktop_click` (`modifiers`) | `src/FlaUI.Mcp.Server/Tools/InputTools.cs:Click` | Ability to hold Ctrl/Shift/Alt during a click for multi-select. | agent-contract.md |
| `desktop_drag` (`endWindow`) | `src/FlaUI.Mcp.Server/Tools/InputTools.cs:Drag` | Support for cross-window dragging. | agent-contract.md |
| `desktop_watch` (`scope`) | `src/FlaUI.Mcp.Server/Tools/WatchTools.cs:Watch` | Constrain `structure_changed` events to a specific subtree. | agent-contract.md |
| `desktop_drain_events` (`max`) | `src/FlaUI.Mcp.Server/Tools/WatchTools.cs:DrainEvents` | Limit the number of events drained. | agent-contract.md |
| `overlay` parameter | `src/FlaUI.Mcp.Server/Tools/InputTools.cs` | Request visual bounding box overlay before acting. | agent-contract.md |
| `IActionOverlay` | `src/FlaUI.Mcp.Core/Interaction/IActionOverlay.cs` | Extensibility point for drawing on-screen bounding boxes. | architecture-and-safety.md |
| `verify` response | `src/FlaUI.Mcp.Server/Tools/InputTools.cs:Type` | Advisory verification failure feedback (`mismatch`). | agent-contract.md |

## 3. Category 2: CLI Verbs & Flags

| Feature | Code location | What it does | Suggested doc home |
|---------|---------------|--------------|--------------------|
| `--overlay` launch flag | `src/FlaUI.Mcp.Server/ServerOptions.cs:ServerOptions.FromArgs` | Enables red intent overlay before mutative actions. | operator-manual.md |
| `--overlay-ms=<ms>` launch flag | `src/FlaUI.Mcp.Server/ServerOptions.cs:ServerOptions.ParseOverlayMs` | Specifies intent overlay duration in milliseconds. | operator-manual.md |
| `--autosound` launch flag | `src/FlaUI.Mcp.Server/ServerOptions.cs:ServerOptions.FromArgs` | Enables TTS spoken cue when target window is obscured. | operator-manual.md |
| `--presence` launch flag | `src/FlaUI.Mcp.Server/ServerOptions.cs:ServerOptions.FromArgs` | Enables coarse human presence sensing. | operator-manual.md |
| `--nearby-secs=<secs>` launch flag | `src/FlaUI.Mcp.Server/ServerOptions.cs:ServerOptions.ParseIntArg` | Idle seconds before user is "nearby". | operator-manual.md |
| `--away-secs=<secs>` launch flag | `src/FlaUI.Mcp.Server/ServerOptions.cs:ServerOptions.ParseIntArg` | Idle seconds before user is "away". | operator-manual.md |
| `--purge-data` option | `src/FlaUI.Mcp.Server/Install/CliRouter.cs:CliRouter.Run` | Deletes the MCP data dir (`~/.flaui-mcp`) during uninstall. | operator-manual.md |
| `--config <path>` option | `src/FlaUI.Mcp.Server/Install/CliRouter.cs:CliRouter.Run` | Overrides the target config file during install verbs. | operator-manual.md |
| `--i-understand` option | `src/FlaUI.Mcp.Server/Install/CliRouter.cs:CliRouter.Run` | Non-interactive risk acceptance (alias for `--accept-risk`). | operator-manual.md |
| `--version`, `-v`, `-h`, `--help` | `src/FlaUI.Mcp.Server/Install/CliRouter.cs:CliRouter.Run` | Displays version and CLI help text (only `--version` is in table). | operator-manual.md |
| `--force-renderer-accessibility` | `src/FlaUI.Mcp.Core/Perception/WakeabilityHint.cs` | Required Chromium launch flag to enable native trees (operator-driven). | operator-manual.md |

## 4. Category 3: Configuration & Environment Variables

| Feature | Code location | What it does | Suggested doc home |
|---------|---------------|--------------|--------------------|
| `FLAUI_MCP_REF_STRICT` | `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` | Break-glass switch forcing lenient resolution on mutative paths. | architecture-and-safety.md *(under-documented: release-notes only)* |
| `FLAUI_MCP_REF_MAXSCOPES` | `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` | Tunes the ancestor fan-out cap for scope gathering (default 512). | architecture-and-safety.md *(under-documented: release-notes only)* |
| `FLAUI_MCP_SELECTOR_MAXNODES` | `src/FlaUI.Mcp.Core/Perception/RefResolveConfig.cs` | Caps the number of nodes a selector walk evaluates to prevent unbounded UIA walks. | architecture-and-safety.md *(UNDOCUMENTED)* |
| `FLAUI_MCP_DATA_DIR` | `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | Overrides the root directory used for generic configs and presence states. | operator-manual.md *(under-documented: release-notes only)* |
| `FLAUI_MCP_STATE_DIR` | `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | Overrides the location for state files like uninstall warnings. | operator-manual.md *(UNDOCUMENTED)* |
| `FLAUI_MCP_AGY_PLUGINS_DIR` | `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | Overrides the target path for agy plugin installation. | operator-manual.md *(UNDOCUMENTED)* |
| `FLAUI_MCP_CLAUDE_CONFIG_DIR` | `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | Overrides the path for Claude's config/skills directory. | operator-manual.md *(UNDOCUMENTED)* |
| `CLAUDE_CONFIG_DIR` | `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | Upstream Claude Code env var honored as fallback if `FLAUI_MCP_CLAUDE_CONFIG_DIR` is not set. | operator-manual.md *(UNDOCUMENTED)* |
| `FLAUI_TTS_TEXT` | `src/FlaUI.Mcp.Server/Attention/TtsSignal.cs` | Internal env var used to pass the spoken cue text to the PowerShell TTS process. | architecture-and-safety.md *(UNDOCUMENTED)* |

## 5. Audit Summary

**Gap Counts by Category:**
- **Category 1 (Tools/Parameters):** 19 findings
- **Category 2 (CLI Verbs/Flags):** 11 findings
- **Category 3 (Config/Env Vars):** 9 findings

**Top 5 Most Important Gaps:**
1. **`timeoutMs` (Cat 1):** The universal timeout dial is critical for agent reliability on slow UIs but entirely absent from the contract.
2. **`verify` Response (Cat 1):** The advisory feedback for synthetic text input is missing, leaving agents unable to handle reactive-editor failures without throwing errors.
3. **`FLAUI_MCP_REF_STRICT` (Cat 3):** The primary break-glass switch to restore functionality on volatile UIA targets is effectively invisible to operators.
4. **`--overlay` / `--autosound` (Cat 2):** These are vital observability flags for the operator to audit agent behavior but are not mentioned in the CLI table.
5. **Cross-window `desktop_drag` (Cat 1):** Essential functionality for complex interactions (like drag-and-drop between applications) is completely hidden.
