# System tray as a driveable surface (`desktop_open_tray`) — design

**ROADMAP Track B, item B2** (`ROADMAP.md`, "B2 — System-tray pseudo-window"). v1.1.0 is cut once this lands.

**Provenance.** Measured on Windows 11 Pro 10.0.26200 (console, 100%) on 2026-11-06; the measurements and the
design negotiation are in `.clavity/seams/tray-first.md` (M1-M17). AGY-FIRST consult: round 0 `NEGOTIATE`
(the peer rejected a tray window handle in favour of tray verbs), round 1 `ALIGNED` on synthesis S1-S5 plus
amendments A1-A4. The owner then added the window handle back on top of the verbs (§1.1).

**Owner scope.** An agent must be able to (a) read the tray, hidden icons included, (b) left-click an icon,
and (c) open an icon's right-click menu and choose an item, e.g. "right-click Docker, choose Quit".

## What Windows 11 actually exposes (measured)

The ROADMAP text names `NotifyIconOverflowWindow`. **It no longer exists** on Windows 11 (M1).

| | Visible icons | Hidden icons |
|---|---|---|
| Host | UIA descendants of `Shell_TrayWnd` (explorer) | a separate top-level explorer window, Name `System tray overflow window.`, class `TopLevelWindowForOverflowXamlIsland`, that exists **only while open** (M3) |
| How to show | always shown | Invoke the chevron button (`aid=SystemTrayIcon`, Name starts `Show Hidden Icons`) |
| Icon element | `Button`, `aid=NotifyItemIcon`, class `SystemTray.NormalButton` | same |
| System icons | `aid=SystemTrayIcon` (Network, Volume, Clock, input indicator, Show Desktop) | — |
| Patterns | Invoke, ScrollItem — **no right-click path in UIA** (M8) | same |
| Identity | the tooltip only: multi-line, localized, changes with state (M4, M10). **No owner process.** | same |

- The chevron is a toggle, and its Name becomes `Show Hidden Icons Hide` while the overflow is open (M14).
  Overflow state is therefore detected by **window presence**, never by the chevron's Name.
- `Shell_TrayWnd` and tray menus are untitled, so `desktop_list_windows` (`WindowManager.EnumTopLevel`,
  `WindowManager.cs:400`) never lists them, and `desktop_open_window` cannot reach them (M5).
- A right-click needs real `SendInput`, so it needs flaui-mcp's input lease (M8).
- The overflow stays open while a menu from one of its icons is up (M12).
- An open menu persisted 10 s and 30 s with no input and stayed readable through UIA (M11).
- `WM_CANCELMODE` does **not** close a tray menu; Escape does (M16).

**Three kinds of right-click result** (M6, M7, M13):

| Kind | Example | What appears | How its items are read |
|---|---|---|---|
| **K1** UIA menu | ShareX (WinForms) | untitled top-level window of the app's process, owned by a hidden app window; UIA `Menu` with `MenuItem`s [Invoke, ExpandCollapse] | UIA |
| **K2** app window | OneDrive | the app's own **titled** window (`Activity Center`); entries are `MenuItem` [Value]; Escape may not close it (M10) | not read as a menu — returned as a handle |
| **K3** classic menu | ZoomIt | class `#32768` window; **UIA sees a Pane with zero children**, raw view too | Win32: `MN_GETHMENU` → `GetMenuItemCount` / `GetMenuString`; positions from `GetMenuItemRect` (M17) |

## 1. Tool surface

Three new tools. All three resolve the taskbar the same way: `FindWindow("Shell_TrayWnd")`, then a handle
through the existing `WindowManager.LazyHandleFor(hwnd, pid)` path (`WindowManager.cs:301`, today `private`;
it becomes `internal`), so no new handle machinery is introduced. No `Shell_TrayWnd` → `WindowNotFound` ("taskbar not available; explorer may be
restarting").

**One tray at a time (F5).** The three tools share one process-wide lock. A second call waits for the first
(bounded by its own `timeoutMs`; `desktop_open_tray` and `desktop_list_tray_icons` take no `timeoutMs` and wait
at most 4000 ms, then fail `Timeout`), so two calls never toggle the overflow or take baselines over each other.

### 1.1 `desktop_open_tray(includeHidden = false)` — ReadOnly, lease-exempt

Returns `{ handle, overflowHandle?, overflowLeftOpen }`.

- `handle`: a `wN` for `Shell_TrayWnd` (the whole taskbar, ~30 control-view elements, M15). Usable with the
  existing perception and interaction tools: `desktop_snapshot`, `desktop_find`, `desktop_invoke`,
  `desktop_click`...
- **Except the window-lifecycle tools.** `desktop_close_window` (`WindowManager.CloseAsync`,
  `WindowManager.cs:671`, calls `w.Close()` unguarded) and `desktop_window_transform` refuse a handle bound to
  `Shell_TrayWnd` or the overflow window with `ElementNotActionable` ("the taskbar cannot be closed, minimized
  or resized"). Sending the taskbar a close request is the classic way to raise Windows' "Shut Down Windows"
  dialog. The refusal keys on the HWND's class, so it also covers a tray handle obtained any other way.
- `includeHidden = true`: opens the overflow if it is closed, and returns `overflowHandle` for it.
  **The overflow is deliberately left open**, because a handle to a window about to be closed is useless.
  `overflowLeftOpen: true` says so. `desktop_open_tray(includeHidden = false)` returns only `handle` and does
  not touch the overflow. To close an overflow, the agent invokes the chevron through the taskbar handle, or
  simply moves on: the overflow closes by itself when the foreground moves.
- The overflow closes by itself when the foreground moves; its handle then fails with `WindowHandleStale`.
  The tool description tells the agent to call `desktop_open_tray(includeHidden = true)` again.

### 1.2 `desktop_list_tray_icons(includeHidden = false)` — ReadOnly, lease-exempt

Returns `{ icons: [{ name, location }], overflowRestored? }`.

- `location`: `"visible"` (`NotifyItemIcon` under `Shell_TrayWnd`), `"system"` (`SystemTrayIcon` under
  `Shell_TrayWnd`, excluding the chevron itself), or `"hidden"` (`NotifyItemIcon` in the overflow).
- `name`: the full tooltip as UIA reports it, after the redaction rules (§3.4).
- `includeHidden = true`: if the overflow is closed, open it, read it, and close it again in a `finally`.
  If it was already open, read it and leave it open. `overflowRestored` reports whether the end state equals
  the start state (present only when `includeHidden` is true).
- Order: visible, then system, then hidden, each in UIA tree order.

### 1.3 `desktop_tray_action(icon, action, menuItem = null, timeoutMs = 4000)` — Destructive

`timeoutMs` is validated and capped like every action timeout (`AutomationDispatcher.ClampActionTimeout`:
negative → `InvalidArguments`, capped at 60000).

Blocked in `--read-only-mode` (`ToolResponse.GuardWrite`).

- `icon`: case-insensitive substring of the tooltip, searched across visible, system and hidden icons (the
  overflow is opened for the search if needed, and restored as in §1.2).
  0 matches → `SelectorNoMatch`; more than 1 → `AmbiguousMatch`, whose message lists every candidate's name.
  **Every result echoes `matchedIcon`** (the full tooltip that matched). A tooltip is set by the app and is
  untrusted: any app can call its icon "Docker". The tool cannot tell them apart (no owner process, M4), so the
  description tells the agent to check `matchedIcon` before trusting the result (F7). `matchedIcon`, and the
  candidate names in an `AmbiguousMatch` message, pass through the same redaction as §3.4.
- `action = "left_click"`: UIA Invoke on the icon. **No lease.**
- **Overflow after an action (both actions, F4).** If the tool opened the overflow, it closes it if it is still
  open. If the overflow was already open, the tool never re-opens it: the action usually moves the foreground
  to the app, which closes the overflow by itself, and re-opening it over the app would undo what the agent
  asked for. `overflowRestored` is reported only when the overflow was open before the call (true = still
  open afterwards).
  It does not try to catch whatever window the app opens; the agent uses `desktop_list_windows`.
- `action = "right_click"`: **lease required** (`InputNotLeased`). §2 describes the whole sequence.

**Result shapes (F8).** Fields not listed for a case are absent, never `null`.

| Case | Fields |
|---|---|
| `left_click` | `matchedIcon`, `overflowRestored`? |
| `right_click`, K1/K3, no `menuItem` | `matchedIcon`, `menuKind: "menu"`, `items`, `dismissed`, `overflowRestored`? |
| `right_click`, K1/K3, `menuItem` selected | `matchedIcon`, `menuKind: "menu"`, `items`, `selected`, `dismissed`, `overflowRestored`? |
| `right_click`, K2 | `matchedIcon`, `menuKind: "window"`, `handle`, `warning`?, `overflowRestored`? |
| any of the above, cleanup trouble | add `warning` |

`items` is `[{ text, enabled, hasSubmenu }]` in menu order. `selected` is the cleaned text of the chosen item.
`dismissed` is `true` when no menu window of the tool's is left open. `warning` is a sentence for the agent.
`action` and `menuItem` are matched case-insensitively. An empty or whitespace-only `icon` or `menuItem` is
`InvalidArguments` (an empty substring would otherwise match everything).
- Any other `action` → `InvalidArguments`. (`double_click` is out of scope, §5.)

## 2. `right_click`, step by step

1. **Resolve** the icon (§1.3). If it is hidden, the overflow is opened and stays open until step 8.
2. **Baseline**: record every *visible* top-level HWND, titled or not (`EnumWindows` + `IsWindowVisible`; no
   title filter).
3. **Right-click** the icon's centre through the existing input pipeline (`InputGuard` →
   `Win32SyntheticInput.MouseClick`): elevation, lease, deny-list, and the hit-test re-verify that the point
   still maps to the taskbar or overflow window immediately before sending.
4. **Poll** every 100 ms, up to `timeoutMs`, for visible top-level windows not in the baseline. Ignore:
   explorer's tooltip host (`Xaml_WindowedPopupClass`), `SysShadow`, the overflow window, and the intent
   overlay (`WindowManager.ShouldSkipTopLevel`).
5. **Classify.** Only a window of the **foreground process** is a candidate (F6): in every measured case the
   right-click result belonged to the process that then held the foreground (M6 K1: the menu's owner; M7 K2:
   the window itself; M13 K3: ZoomIt's own window). A menu another app opens meanwhile is ignored. Every UIA
   read of a candidate runs on the action STA under the call's remaining `timeoutMs`
   (`AutomationDispatcher.RunActionAsync`), so a hung app cannot block the tool forever (F9). Kinds,
   strongest first:
   - **K3**: class `#32768` and `SendMessage(MN_GETHMENU)` returns a non-null HMENU;
   - **K1**: its UIA control view has at least one `MenuItem` descendant;
   - **K2**: it has a non-empty title **and is the foreground window**. In both measured cases the right-click
     result took the foreground (M6: the K1 menu's owner; M7: OneDrive's window). Requiring it keeps an
     unrelated titled window that happens to appear during the poll (a toast, another app) from being taken
     for the menu.
   **Decide only when the picture is stable (F1):** two consecutive polls see the same **non-empty** set of new
   candidate windows and the same item counts. An empty set is never "stable": the poll continues until
   `timeoutMs`, so a slow app is not cut off after 200 ms. Then the strongest kind present wins. K2 is chosen only when no K1/K3
   candidate exists at that point, so a slow menu cannot lose to a window that classified earlier.
   Nothing stable by `timeoutMs` → `Timeout` ("no menu appeared"), after the cleanup in step 8, which also
   covers a menu that appears late (F2).
6. **Deny-list**: if the winning window's process is denied (`PerceptionPolicy.IsDenied`, which fails closed on
   an unreadable process), dismiss (step 8) and fail `TargetDenied`. No item is read first.
7. **Read items** (K1, K3). Clean each label: drop `&` accelerator markers, drop everything from the first tab
   (shortcut text), trim. Drop separators from what is **returned**. Each returned item carries `{ text, enabled, hasSubmenu }`;
   internally each K3 item also keeps its **raw position** in the HMENU (separators included), because
   `GetMenuItemRect` and `GetMenuState` index by raw position. Selecting by the filtered index would click the
   wrong item in any menu with a separator above the target.
   - K1: `enabled` = UIA IsEnabled; `hasSubmenu` = ExpandCollapse supported and the item has children or
     ExpandCollapseState is not `LeafNode`.
   - K3: `enabled` = not `MF_GRAYED`/`MF_DISABLED` (`GetMenuState`); `hasSubmenu` = `MF_POPUP`.
   - **Without `menuItem`**: return `{ menuKind: "menu", items }` and dismiss (step 8).
   - **With `menuItem`**: match case-insensitively against the cleaned text — exact equality first; if none,
     substring. 0 → `SelectorNoMatch` (message lists the items); more than 1 → `AmbiguousMatch`; disabled →
     `ElementNotActionable`; `hasSubmenu` → `ElementNotActionable` ("submenus are not supported"). Every error
     path dismisses first.
     - K1: UIA Invoke on the `MenuItem`.
     - K3: left-click the centre of `GetMenuItemRect(NULL, hmenu, rawPosition)` through the input pipeline, with
       the hit-test re-verify that the point maps to the `#32768` window. A NULL window handle is MEASURED to
       work for a tray `#32768` menu (M17: `ok=True`, four real rects).
     - If the menu window is gone before the selection is sent → `ElementDisappearedDuringAction`.
     - Success returns `{ menuKind: "menu", items, selected: "<cleaned text>" }`; the menu is gone afterwards
       (verified; if it is still present, `dismissed: false` and a `warning`).
   - **K2**: return `{ menuKind: "window", handle, warning }`, where `handle` comes from `LazyHandleFor`. If
     `menuItem` was given, the warning says the target opened an application window and nothing was selected:
     use `desktop_snapshot` / `desktop_click` on the handle. A K2 window is not dismissed.
8. **Cleanup** (in a `finally`), on every path including errors:
   - One last scan for new K1/K3 windows of the foreground process, so a menu that appeared after the poll
     ended is not left behind (F2). If any is up, send Escape through the input pipeline and verify it is
     gone → `dismissed: true|false`. The keystroke's expected target is the **current foreground root**
     (`GetForegroundRoot()` read just before sending), not the menu window: `KeyChord` re-verifies the
     foreground against its target (`Win32SyntheticInput.cs:43`), and for K1 the foreground is the menu's hidden
     owner (M6).
   - Handle the overflow as §1.3 says → `overflowRestored`.
   - **Cleanup never replaces the primary result or error (F3).** If the Escape itself is refused (the lease
     lapsed, the session locked), the call still returns its primary outcome, with `dismissed: false` and a
     `warning` naming why; an error's message gets the same sentence appended.

## 3. Safety

1. **Lease**: only `right_click` (including its Escape and K3 click) needs it. `left_click`, listing and
   opening do not, because they use UIA Invoke.
2. **Read-only mode**: `desktop_open_tray` and `desktop_list_tray_icons` stay available. Toggling the overflow
   is transient layout disclosure that the tool undoes (§1.2), which both reviewers accepted as read-only.
   `desktop_open_tray(includeHidden = true)` leaves it open by design and says so.
3. **Deny-list**: §2 step 6. A password manager's tray **menu** is refused before anything is read.
4. **Redaction of tooltips**: every tooltip the tools return — `desktop_list_tray_icons`' `name`,
   `desktop_tray_action`'s `matchedIcon`, and candidate names in an `AmbiguousMatch` message — passes through the same name-redaction
   path a snapshot uses (`ElementContent` + `RedactionRules`), evaluated against the element's **real owner,
   explorer**. Consequence, documented in the tool description and `docs/agent-contract.md`: a rule scoped to
   a password manager's process does **not** hide that manager's tray tooltip, because UIA gives no owner for
   the icon. A global rule, or a rule scoped to `explorer`, does. The same is already true of a
   `desktop_snapshot` of the taskbar, so the list tool opens no new channel.
5. **Concurrent human input**: the hit-test re-verify (steps 3 and 7) refuses to click when the point no longer
   belongs to the expected window; a menu closed under us is `ElementDisappearedDuringAction`.

## 4. Errors (all existing `ToolErrorCode`s)

| Situation | Code |
|---|---|
| No `Shell_TrayWnd` | `WindowNotFound` |
| `icon` matches nothing / more than one | `SelectorNoMatch` / `AmbiguousMatch` (candidates listed) |
| unknown `action` | `InvalidArguments` |
| `right_click` without a lease | `InputNotLeased` |
| locked / disconnected session | `InputDesktopUnavailable` |
| no menu settled within `timeoutMs` | `Timeout` |
| menu owned by a denied process | `TargetDenied` |
| `menuItem` matches nothing / more than one | `SelectorNoMatch` / `AmbiguousMatch` (items listed) |
| `menuItem` disabled, or opens a submenu | `ElementNotActionable` |
| menu vanished before selection | `ElementDisappearedDuringAction` |
| `desktop_tray_action` in read-only mode | `WriteBlockedReadOnly` |

## 5. Out of scope for v1 (documented, not silently unsupported)

- **Submenus** (§2 step 7 refuses them explicitly).
- **`double_click`**.
- **Secondary-monitor taskbars** (`Shell_SecondaryTrayWnd`); not measured with two monitors.
- **Auto-hidden taskbar**: not measured. Expected: the icon is off-screen and the right-click is refused with
  `ElementNotActionable`; the plan measures it before relying on that.
- **Selecting inside a K2 window**: the agent uses the returned handle with the existing tools.

## 6. Docs and surface

- `docs/agent-contract.md`: a tool-catalog section for the three tools, the K1/K2/K3 result shapes, the
  tooltip-redaction limit (§3.4), and the out-of-scope list (§5).
- `CHANGELOG.md` `[Unreleased]`: Added.
- `ROADMAP.md` B2: correct the stale `NotifyIconOverflowWindow` text and mark shipped.
- The plan enumerates every test or doc that pins the tool list or tool count (e.g. the JSON-shape tripwires)
  and updates it; it does not guess.

## 7. Testing

**Fixture.** A new app, `test/FlaUI.Mcp.TrayFixture`, adds its own tray icon. Arguments:
`--kind uia|classic|window` (K1 WinForms `ContextMenuStrip` / K3 `TrackPopupMenu` / K2 a titled window) and
`--tooltip "FlaUI Tray Fixture <guid>"`, unique per test so a test can never pass by matching another app's
icon, **including a ghost of an earlier fixture (F11)**: a tray icon is not removed when its process is
killed, so the fixture exits on a registered window message and removes its icon (`NotifyIcon.Dispose` /
`NIM_DELETE`), and tests only ever kill it as a last resort. Its menu has fixed items, including one disabled item and one submenu. It **logs every command it
receives** to `%TEMP%\flaui-trayfixture-<pid>.log` (the TestApp's event-log pattern), and tests assert that log,
not only the tool's reply. The belief that Windows 11 places a new tray icon in the **overflow** is **not measured yet** (Q5).
Each hidden-path test therefore asserts the fixture icon's `location` first and fails, with a message
naming Q5, if it is not `hidden`: a test that silently ran the visible path would prove nothing about the
hidden one (F10). The visible path is covered through system icons (the Clock is always present).

| Category | Tests |
|---|---|
| headless (CI) | menu-kind classification from window facts; label cleaning (`&`, tab accelerators, separators); icon/item matching (equality, then substring, then ambiguity); overflow-restore decision; deny-list decision |
| `Desktop` | list contains the fixture icon as `hidden` with its exact tooltip; overflow restored from both start states (closed and open); `desktop_open_tray` handles work and the overflow handle goes stale once closed; `left_click` reaches the fixture (its log) |
| `Desktop` + `SyntheticInput` | `right_click` on a **visible** icon (the fixture promoted to the visible tray, Q7) as well as a hidden one; `right_click` lists the expected items for K1 and K3; selecting an item lands in the fixture log and the menu is gone; K2 returns a window handle; errors: no match, ambiguous (two fixture icons sharing a prefix), disabled item, submenu item |

**Mutation checks** (each must turn at least one test red): remove the K3 Win32 path; skip the overflow restore;
accept the first of an ambiguous match; report `selected` without sending the selection.

## 8. Open measurements — resolved in the plan, before the code that depends on them

| # | Question | Why it matters | Where |
|---|---|---|---|
| Q1 | Does UIA Invoke on a K1 `MenuItem` (ShareX) actually run the command? | §2 step 7 K1 path | plan, first task |
| Q2 | Is `GetMenuItemRect` in physical pixels at 125% (RDP)? | K3 click lands on the item | plan; if not, the K3 tests also get a `ScaledDisplay` variant |
| Q3 | Auto-hidden taskbar behaviour | §5 expected error | plan |
| Q4 | Does a `TrackPopupMenu` fixture menu classify as K3 the way ZoomIt's does? | the fixture must exercise the real K3 path, not a look-alike | plan, fixture task |
| Q5 | Does a new tray icon land in the overflow on Windows 11? | which path the fixture exercises (F10) | plan, fixture task |
| Q7 | Can a test promote the fixture icon to the visible tray (e.g. `HKCU\Control Panel\NotifyIconSettings\<id>\IsPromoted`), and restore it? | the visible right-click test (panel r1, 5.1). If it cannot, the gap is recorded under Stand-downs as UNVERIFIED-ACCEPTED, not hidden | plan, fixture task |
| Q6 | Is the right-click result's process the foreground process for every kind, including the fixture's? | §2 step 5's candidate rule (F6) | plan, measured on K1/K2/K3 fixtures |

## Stand-downs

None so far.
