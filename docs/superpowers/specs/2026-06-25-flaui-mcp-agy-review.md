# FlaUI.Mcp Design Review

**Reviewer:** AGY (Grok)  
**Date:** 2026-06-25  
**Scope:** Review of provided design spec ONLY. Greenfield context assumed.

## Executive Summary

The spec is strong, mature, and production-minded — excellent STA threading, ref re-resolution strategy, hybrid perception, session lifecycle, and error modeling. It avoids many common UIA pitfalls. However, there are targeted gaps in tool coverage, edge-case resilience, and opportunities for desktop-native superpowers beyond a straight Playwright analog.

Findings are grouped by lens, each with **Severity/Impact**, **What**, **Why**, **Concrete Suggestion**.

## 1. EXHAUSTIVENESS — Gaps, Underspecifications, and Agent Wall-Hitters

### Finding 1.1: Missing UIA Control Pattern Coverage
**Severity/Impact:** High (core interaction surface incomplete; agents will hit PATTERN_UNSUPPORTED frequently on tables/grids/text fields).  
**What:** Tool catalog covers Invoke/Value/Toggle/ExpandCollapse/Selection/ScrollItem but omits major patterns: Grid/GridItem, Table/TableItem, Text (range selection, caret), Window/Transform (move/resize/minimize/maximize), VirtualizedItem, ItemContainer, MultipleView.  
**Why:** Real apps (Excel, Outlook, VS, custom LOB) rely heavily on these. Without them, agents fall back to brittle synthetic clicks/typing or fail.  
**Concrete Suggestion:** Add `desktop_grid_get_cell`, `desktop_grid_select`, `desktop_text_get_selection`/`desktop_text_set_caret`, `desktop_window_transform` (with bounds), `desktop_virtualized_scroll`. Prioritize Text and Grid for v1. Document fallback behavior explicitly in Interactor.

### Finding 1.2: No Explicit Support for Custom/Non-Standard Controls or Legacy Win32
**Severity/Impact:** Medium-High (many enterprise apps use custom-drawn or old HWND-based controls).  
**What:** Spec assumes modern UIA exposure; no mention of MSAA fallback, raw HWND manipulation, or SendMessage/PostMessage for controls that expose neither.  
**Why:** UIA coverage is incomplete even on Microsoft apps; agents will deadlock on "invisible" elements.  
**Concrete Suggestion:** In SnapshotEngine and Interactor, add optional MSAA probe layer (via FlaUI or direct). Add `desktop_raw_send_message` tool (with safety warnings). Or at least document the failure mode and suggest agent workarounds (e.g., coordinate clicks).

### Finding 1.3: Snapshot Pruning and Token Management Underspecified
**Severity/Impact:** Medium (context window blowouts in large apps).  
**What:** `interactiveOnly` is a good start but details on heuristics, depth limits, and customization are light. No way to request "just this subtree" beyond `root` ref.  
**Why:** Enterprise apps (SAP, Oracle forms) have massive trees; agents need tunable perception.  
**Concrete Suggestion:** Expose `pruneStrategy` enum or filters (e.g., by ControlType list, visibility, bounding box). Add `desktop_snapshot_stats` returning node counts/tokens estimate before full snapshot.

### Finding 1.4: Coordinate System and Vision Path Edge Cases
**Severity/Impact:** Medium (vision path fragility).  
**What:** Screenshot metadata good, but no handling for occluded windows, layered windows (AlwaysOnTop), or multi-monitor virtual coords. `xPct/yPct` helps but not for partial captures. No tool to get element bounds in screenshot space.  
**Why:** Agents using vision will misclick on real desktops.  
**Concrete Suggestion:** Add `desktop_get_bounds` `{window|ref}` returning screenshot-relative rect. Support `captureRect` param in screenshot for subregions. Explicitly document multi-monitor behavior (primary only? raw screen coords?).

### Finding 1.5: Process/Launch Lifecycle Gaps
**Severity/Impact:** Medium (common agent failure modes).  
**What:** `desktop_launch_app` handles splash but no UAC prompt detection, elevation request, or console apps. No `desktop_attach_to_process` for already-running non-windowed or service-proxied apps. Kill policy details light for multi-client.  
**Why:** Agents will orphan processes or fail on common launch scenarios.  
**Concrete Suggestion:** Add `uacPromptDetected` error. Support `desktop_list_processes` and `desktop_attach`. Clarify kill policy per-transport with examples.

### Finding 1.6: Error Handling and Recoverability Holes
**Severity/Impact:** Medium (agent loops can get stuck).  
**What:** Good envelope design, but missing `ELEMENT_DISAPPEARED_DURING_ACTION`, timeout granularity per tool, and retry guidance. No standard "retry snapshot then retry action" pattern documented.  
**Why:** UIA is flaky under load/mutation.  
**Concrete Suggestion:** Standardize error payloads with `suggestedRecovery` field (e.g., "snapshot+retry"). Add `desktop_wait_for_stable` tool.

### Finding 1.7: Testing Strategy Completeness
**Severity/Impact:** Low-Medium (but critical for reliability).  
**What:** Strong core tests, but no mention of DPI scaling test matrix, high-integrity target tests, or synthetic input under varying Windows themes/accessibility settings.  
**Why:** These cause flaky CI or user bugs.  
**Concrete Suggestion:** Add matrix tests for 100%/150%/200% DPI, Win10/11, and elevated vs non-elevated targets.

### Finding 1.8: HTTP Transport Security Underspecified
**Severity/Impact:** High for shared use.  
**What:** Localhost default + warning good, but no mention of CORS, rate limiting, or per-connection isolation beyond SessionManager.  
**Why:** Risk of cross-client interference or abuse.  
**Concrete Suggestion:** Document mandatory auth middleware for non-localhost. Per-connection ref registries.

## 2. CREATIVITY — Desktop-Native Capabilities Beyond Playwright

### Finding 2.1: Event Subscription / Change Notification System
**Severity/Impact:** High value (transforms agent polling into reactive). YAGNI risk: Medium (adds complexity).  
**What:** No structured events for "element changed", "window appeared", "property changed".  
**Why:** Desktop is event-rich (UIA AutomationEvent/PropertyChanged). Agents waste tokens polling snapshots. Enables "watch this button" patterns.  
**Concrete Suggestion:** Add `desktop_subscribe` / `desktop_unsubscribe` with event types (InvokeCompleted, PropertyChanged, StructureChanged). Deliver via SSE (HTTP) or callback tools. Start with StructureChanged + ValuePattern.

### Finding 2.2: Semantic Snapshot Diffs
**Severity/Impact:** High value. YAGNI risk: Low.  
**What:** Agents must manually compare snapshots.  
**Why:** "What changed since last snapshot?" is a top agent need for verification.  
**Concrete Suggestion:** `desktop_snapshot_diff` `{window, previousSnapshotId?}` returning structured delta (added/removed/changed refs with summaries). Leverage RefRegistry history.

### Finding 2.3: OCR / Hybrid Vision Fallback
**Severity/Impact:** Medium-High value for custom UIs. YAGNI risk: Medium.  
**What:** Pure UIA + screenshot; no text extraction from images.  
**Why:** Many apps (games, Electron with canvas, legacy) have zero UIA. OCR bridges the gap.  
**Concrete Suggestion:** Optional Tesseract/PaddleOCR integration in VisionCapture. New tool `desktop_ocr` on screenshot or ref. Flag as opt-in dependency.

### Finding 2.4: Multi-Window Orchestration Primitives
**Severity/Impact:** Medium value. YAGNI risk: Low.  
**What:** Handles are per-window but no built-in "arrange windows", "capture all", or "find related windows by process".  
**Why:** Desktop workflows often span multiple apps/windows (e.g., browser + desktop app).  
**Concrete Suggestion:** `desktop_arrange_windows` (tile/cascade), `desktop_find_related_windows` by pid/ancestor. `desktop_global_snapshot` for overview.

### Finding 2.5: Clipboard, Shell, and System Integration
**Severity/Impact:** Medium value. YAGNI risk: High (scope creep).  
**What:** No clipboard read/write, shell execute, notification area, taskbar pinning.  
**Why:** Powerful for end-to-end flows (copy data from app A to app B).  
**Concrete Suggestion:** Minimal `desktop_clipboard` tool. Defer shell integration to v2 unless core use-case demands.

### Finding 2.6: Accessibility Metadata Enrichment
**Severity/Impact:** Low-Medium. YAGNI risk: Low.  
**What:** Snapshots show basic patterns but could expose more UIA properties (help text, LabeledBy, etc.).  
**Why:** Improves agent reasoning without vision.  
**Concrete Suggestion:** Richer snapshot format with optional `fullProperties` flag. Include bounding rects always.

## Overall Recommendations

**Prioritize for v1:**
- Grid/Text/Window patterns (Finding 1.1)
- Better error recovery + bounds (1.4, 1.6)
- Event subscriptions if HTTP transport is primary (2.1)

**Strengths to Preserve:**
- Option-C ref engine
- Dedicated STA dispatcher
- Hybrid perception
- SessionManager lifecycle

This design positions FlaUI.Mcp as a genuine leap for desktop agents. Implementing the high-impact exhaustiveness fixes will prevent most agent frustrations. Creative additions like events and diffs would make it stand out significantly. 

Ready for implementation planning.