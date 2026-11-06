# B2 System Tray (`desktop_open_tray` / `desktop_list_tray_icons` / `desktop_tray_action`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the three tray tools specified in `docs/superpowers/specs/2026-11-06-b2-system-tray-design.md` (the SPEC below), with every open measurement Q1–Q9 answered before the code that depends on it is written.

**Architecture:** A new `FlaUI.Mcp.Core.Tray` namespace holds pure decision units (label cleaning, icon/item matching, menu-kind classification, overflow-restore decision), two readers (UIA tray icons, Win32 classic menus), and one orchestrator `TrayManager` that owns the process-wide tray lock and the right-click sequence. `TrayTools` in the Server project is a thin `[McpServerToolType]` over it, following `WindowTools`/`InteractionTools`. `WindowManager` gains an `internal` `LazyHandleFor` and a tray-window refusal in close/transform. A new WinForms fixture app `test/FlaUI.Mcp.TrayFixture` adds a tray icon of kind K1/K2/K3 for the Desktop tests.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), FlaUI.UIA3 5.0.0, ModelContextProtocol server SDK, xunit 2.9 + Xunit.SkippableFact, Win32 P/Invoke (user32), PowerShell 7 probes for measurements.

---

## Ground rules for every task

- **Read the SPEC section cited by the task before editing.** Section numbers (§1.3, §2 step 5, …) refer to the spec.
- **Gate commands (repo's own, never stricter):**
  - Build: `dotnet build FlaUI.Mcp.slnx -c Debug` → expect `0 Warning(s)` / `0 Error(s)` (warnings are errors, `Directory.Build.props`).
  - Headless: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"` → expect `Passed!` with 0 failed.
  - Desktop (needs an unlocked interactive session; ask the operator which session, RDP is 125 %, console is 100 %): `dotnet test test/FlaUI.Mcp.Tests --filter "Category=Desktop&FullyQualifiedName~Tray"`.
  - Lease-bearing tests use `[SkippableFact]` + `Skip.If(InputLocked(), ...)` exactly like `test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs:35-40`; they run inside the Desktop filter and skip without a lease. Grant one with `flaui-mcp unlock --minutes 30` on the host.
- **The redaction source sweep** (`test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs`, headless, runs in CI) flags **every** `.Name` member access in `src/` (RULE 1, line 568) unless the enclosing member is allowlisted, forbids a `SensitivityClassifier` parameter defaulting to `null` (RULE 4b), and forbids the literal `[REDACTED]` outside `ElementContent` (RULE 5). Therefore, in all new `src/` code:
  - read an element's name **only** through `ElementContent.Name(el, classifier, processName).Text` (`src/FlaUI.Mcp.Core/Perception/ElementContent.cs:38`);
  - never declare or read a property called `Name` on a tray record: call it `Tooltip` or `Label`;
  - take `SensitivityClassifier classifier` as a required parameter.
- **New `.cs` files start with the license header** in `header.txt` (the pre-push `addlicense -check` fails otherwise):
  ```csharp
  // Copyright (c) 2026 Costas Kirgoussios
  //
  // This software is licensed under the PolyForm Noncommercial License 1.0.0.
  // You may obtain a copy of the License at:
  // https://polyformproject.org/licenses/noncommercial/1.0.0/
  ```
- **Commit subjects are conventional** (`feat|fix|docs|chore|test|refactor|perf|ci|build|style|revert`, `scripts/check-commit-msg.ps1:50`). Every commit ends with the attribution line the session reminder gives.
- **Tool descriptions** must be ≤ 1500 chars (`ToolTrapFactInvariantTests.DescriptionBudget`), every `Destructive` tool must say `Blocked in --read-only-mode` and either `Requires an active input lease` or `NO input lease required` (`ToolReadOnlyInvariantTests:88-130`), and every tool is exactly one of `ReadOnly = true` / `Destructive = true`.
- **Tool classes are constructed with `null` deps** by `ToolReadOnlyInvariantTests.Construct` (line 136); a Destructive tool must reach `ToolResponse.GuardWrite` before touching any field.
- **Measurement results are recorded in the SPEC §8 table**, in the row's `Where` cell, as `MEASURED <date>: <one-line answer>` (or `UNVERIFIED-ACCEPTED: <why>`), and committed with `docs(spec): B2 Qn measured`. The probe scripts live in `%TEMP%\flaui-tray-probes\` (not in the repo); the plan carries their full text.
- **Probes that send input** (right-click, Escape) use `mouse_event`/`keybd_event` from PowerShell, which needs the interactive session the operator named. They touch real tray apps on the operator's machine (ShareX, ZoomIt, OneDrive were present on 2026-11-06); if one is missing, say so and ask which app to use instead.

---

## File structure

**Create**
| Path | Responsibility |
|---|---|
| `src/FlaUI.Mcp.Core/Tray/TrayWin32.cs` | All tray-specific P/Invoke: `FindWindowW`, `EnumWindows`, `IsWindowVisible`, `IsWindow`, `GetClassNameW`, `GetWindowTextLengthW`, `GetWindowThreadProcessId`, `GetForegroundWindow`, `SendMessageW` (`MN_GETHMENU`), `GetMenuItemCount`, `GetMenuStringW`, `GetMenuState`, `GetMenuItemRect`. Constants. |
| `src/FlaUI.Mcp.Core/Tray/MenuLabel.cs` | Pure: `Clean(string raw)` (§2 step 7). |
| `src/FlaUI.Mcp.Core/Tray/TrayMatch.cs` | Pure: icon substring match → `MatchOutcome`; item equality-then-substring match. |
| `src/FlaUI.Mcp.Core/Tray/MenuKind.cs` | `enum MenuKind { None, Menu, Window }` + `CandidateFacts` record + pure `TrayMenuClassifier.Classify` (§2 step 5). |
| `src/FlaUI.Mcp.Core/Tray/OverflowRestore.cs` | Pure decision: should the tool close the overflow now, and what `overflowRestored` to report (§1.2, §1.3). |
| `src/FlaUI.Mcp.Core/Tray/TrayLocator.cs` | Win32+UIA: find `Shell_TrayWnd`, find the overflow window, find the chevron by the rule in SPEC "Finding the chevron", open/close the overflow with confirmation. |
| `src/FlaUI.Mcp.Core/Tray/TrayIconReader.cs` | UIA read of icons (`TrayIconInfo` with `Tooltip`, `Location`, bounds, runtime id), names via `ElementContent.Name`. |
| `src/FlaUI.Mcp.Core/Tray/ClassicMenuReader.cs` | Win32 read of a `#32768` menu: items with raw position, enabled, hasSubmenu, rect. |
| `src/FlaUI.Mcp.Core/Tray/UiaMenuReader.cs` | UIA read of a K1 menu window: items with the `AutomationElement`, enabled, hasSubmenu. |
| `src/FlaUI.Mcp.Core/Tray/TrayManager.cs` | Orchestrator: lock, `OpenTrayAsync`, `ListIconsAsync`, `LeftClickAsync`, `RightClickAsync` (§2 steps 1–8). Result records. |
| `src/FlaUI.Mcp.Server/Tools/TrayTools.cs` | The three MCP tools (§1). |
| `test/FlaUI.Mcp.TrayFixture/FlaUI.Mcp.TrayFixture.csproj`, `Program.cs`, `TrayForm.cs` | WinForms fixture: `--kind uia|classic|window --tooltip "<text>"`, command log, graceful exit on a registered message (§7). |
| `test/FlaUI.Mcp.Tests/Tray/MenuLabelTests.cs`, `TrayMatchTests.cs`, `TrayMenuClassifierTests.cs`, `OverflowRestoreTests.cs`, `TrayToolsContractTests.cs` | Headless. |
| `test/FlaUI.Mcp.Tests/Tray/TrayFixtureHost.cs` | Test helper: launch/stop the fixture, read its log. |
| `test/FlaUI.Mcp.Tests/Tray/TrayDesktopTests.cs` | `Desktop`: open/list/left-click/handle refusal. |
| `test/FlaUI.Mcp.Tests/Tray/TrayRightClickTests.cs` | `Desktop` + `SyntheticInput`: right-click K1/K2/K3, selection, errors. |

**Modify**
| Path | Change |
|---|---|
| `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` | `LazyHandleFor` → `internal` (line 302); new `internal Task<string> MintHandleAsync(IntPtr hwnd, int pid)`; tray-window refusal in `CloseAsync` (671) and `WindowTransformAsync` (697). |
| `src/FlaUI.Mcp.Server/Program.cs` | register `TrayManager` and `TrayTools` after line 182. |
| `src/FlaUI.Mcp.Server/Tools/InputTools.cs:413` | `desktop_click` description: route tray right-clicks to `desktop_tray_action`. |
| `FlaUI.Mcp.slnx` | add the fixture project. |
| `.claude/skills/driving-flaui-mcp/SKILL.md` (the embedded seed, `FlaUI.Mcp.Server.csproj:13`) then `just plugin` to regenerate `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` | one-line tray route. |
| `docs/agent-contract.md` | new "System Tray" catalog section; error-code rows; limitation bullet. |
| `CHANGELOG.md` `[Unreleased]` | Added. |
| `ROADMAP.md:103-107` | B2 corrected and marked shipped. |
| `docs/superpowers/specs/2026-11-06-b2-system-tray-design.md` §8 | measurement results. |

---

## Part A — measurements that need no fixture (Q1, Q9, Q2, Q3, Q8)

### Task 1: Probe harness and Q1 (does UIA Invoke on a K1 MenuItem run the command?)

SPEC §8 Q1. Target: ShareX (K1, WinForms `ContextMenuStrip`). The item invoked is **"Open main window"**: observable (a titled ShareX window appears) and reversible (close it).

**Files:** none in the repo. Probe: `%TEMP%\flaui-tray-probes\common.ps1`, `q1.ps1`.

- [ ] **Step 1: Confirm the session.** Ask the operator which session you are on (console = 100 %, RDP = 125 %). Q1 works on either. Record it in the result line.

- [ ] **Step 2: Write the shared probe library** to `$env:TEMP\flaui-tray-probes\common.ps1`:

```powershell
# common.ps1 — shared by the B2 probes. Dot-source it.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class T {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr m);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuString(IntPtr m, uint id, StringBuilder s, int n, uint f);
  [DllImport("user32.dll")] public static extern uint GetMenuState(IntPtr m, uint id, uint f);
  [DllImport("user32.dll")] public static extern bool GetMenuItemRect(IntPtr h, IntPtr m, uint i, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPhysicalPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public static List<IntPtr> Visible(){ var l=new List<IntPtr>(); EnumWindows((h,_)=>{ if(IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static string D(IntPtr h){ uint p; GetWindowThreadProcessId(h,out p); var c=new StringBuilder(128); GetClassName(h,c,128); var t=new StringBuilder(128); GetWindowText(h,t,128);
    string n="?"; try{ n=System.Diagnostics.Process.GetProcessById((int)p).ProcessName; }catch{} return string.Format("0x{0:X} cls='{1}' title='{2}' proc={3}({4}) owner=0x{5:X}", h.ToInt64(), c, t, n, p, GetWindow(h,4).ToInt64()); }
  public struct POINT { public int X, Y; }
  public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
}
"@
[void][T]::SetProcessDpiAwarenessContext([IntPtr]-4)   # per-monitor v2: physical pixels everywhere
$script:A = [System.Windows.Automation.AutomationElement]
$script:P = [System.Windows.Automation.InvokePattern]
function Get-Tray { $A::FromHandle([T]::FindWindow('Shell_TrayWnd',$null)) }
function Get-Overflow { $A::RootElement.FindFirst('Children',(New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty,'TopLevelWindowForOverflowXamlIsland'))) }
function Get-Chevron {
  # SPEC "Finding the chevron": FIRST element in tree order with aid=SystemTrayIcon AND class SystemTray.NormalButton.
  $all = (Get-Tray).FindAll('Descendants',(New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty,'SystemTrayIcon')))
  foreach ($e in $all) { if ($e.Current.ClassName -eq 'SystemTray.NormalButton') { return $e } }
  return $null
}
function Open-Overflow { if (-not (Get-Overflow)) { (Get-Chevron).GetCurrentPattern($P::Pattern).Invoke(); Start-Sleep -Milliseconds 1200 }; Get-Overflow }
function Close-Overflow { if (Get-Overflow) { (Get-Chevron).GetCurrentPattern($P::Pattern).Invoke(); Start-Sleep -Milliseconds 600 } }
function Find-Icon([string]$Like, [switch]$Overflow) {
  $root = if ($Overflow) { Open-Overflow } else { Get-Tray }
  $root.FindAll('Descendants',(New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty,'NotifyItemIcon'))) | ? { $_.Current.Name -like "*$Like*" } | Select-Object -First 1
}
function Invoke-RightClick($icon) {
  $r=$icon.Current.BoundingRectangle; $cx=[int]($r.X+$r.Width/2); $cy=[int]($r.Y+$r.Height/2)
  $p=New-Object T+POINT; [void][T]::GetCursorPos([ref]$p)
  [void][T]::SetCursorPos($cx,$cy); Start-Sleep -Milliseconds 150
  [T]::mouse_event(8,0,0,0,[IntPtr]::Zero); [T]::mouse_event(16,0,0,0,[IntPtr]::Zero)
  Start-Sleep -Milliseconds 1200
  [void][T]::SetCursorPos($p.X,$p.Y)
}
function Send-Escape { [T]::keybd_event(0x1B,0,0,[IntPtr]::Zero); [T]::keybd_event(0x1B,0,2,[IntPtr]::Zero); Start-Sleep -Milliseconds 500 }
```

- [ ] **Step 3: Write `q1.ps1`:**

```powershell
param([string]$IconName='ShareX', [string]$Item='Open main window', [switch]$Overflow)
. "$env:TEMP\flaui-tray-probes\common.ps1"
$icon = Find-Icon $IconName -Overflow:$Overflow; if (-not $icon) { "icon '$IconName' not found"; exit 1 }
$before = [T]::Visible()
Invoke-RightClick $icon
$new = @([T]::Visible() | ? { $before -notcontains $_ })
"new windows after right-click: $($new.Count)"; $new | % { "  " + [T]::D($_) }
$mi = $null
foreach ($h in $new) { $e=$A::FromHandle($h)
  $mi = $e.FindAll('Descendants',(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem))) | ? { $_.Current.Name -eq $Item } | Select-Object -First 1
  if ($mi) { break } }
if (-not $mi) { "item '$Item' not found via UIA; sending Escape"; Send-Escape; exit 1 }
"invoking '$($mi.Current.Name)' via UIA InvokePattern"
$before2 = [T]::Visible()
$mi.GetCurrentPattern($P::Pattern).Invoke()
Start-Sleep -Milliseconds 1500
"menu windows still visible: " + @($new | ? { [T]::IsWindowVisible($_) }).Count
"windows that appeared after Invoke:"; @([T]::Visible() | ? { $before2 -notcontains $_ }) | % { "  " + [T]::D($_) }
Close-Overflow
```

- [ ] **Step 4: Run it** from the named session: `pwsh -NoProfile -File $env:TEMP\flaui-tray-probes\q1.ps1`. Expected if Q1 = yes: `menu windows still visible: 0` and a new ShareX titled window listed under "windows that appeared after Invoke". Close that ShareX window afterwards (it is the operator's app). Run twice; both must agree.

- [ ] **Step 5: Record.** Edit SPEC §8 row Q1 `Where` cell: `MEASURED 2026-11-06 (<session>): UIA Invoke on 'Open main window' <ran the command / did not>; menu closed by itself: <yes/no>`. If NO: the K1 selection path in Task 17 must click the item's centre through the input pipeline instead (same as K3 but hit-test root = the menu window); write that decision in the same cell.

- [ ] **Step 6: Commit:** `git add docs/superpowers/specs/2026-11-06-b2-system-tray-design.md && git commit -m "docs(spec): B2 Q1 measured"`.

### Task 2: Q9 (does the hit-test resolve a K3 item point to the `#32768` window?)

SPEC §8 Q9, §2 step 7 K3. Target: ZoomIt (K3), in the overflow on the reference machine.

- [ ] **Step 1: Write `q9.ps1`:**

```powershell
param([string]$IconName='ZoomIt', [switch]$Overflow=$true)
. "$env:TEMP\flaui-tray-probes\common.ps1"
$icon = Find-Icon $IconName -Overflow:$Overflow; if (-not $icon) { "icon '$IconName' not found"; exit 1 }
Invoke-RightClick $icon
$m32 = [T]::FindWindow('#32768',$null); "#32768 hwnd=0x{0:X}" -f $m32.ToInt64()
if ($m32 -eq 0) { "no classic menu"; Send-Escape; exit 1 }
$h = [T]::SendMessage($m32, 0x01E1, [IntPtr]::Zero, [IntPtr]::Zero); $n=[T]::GetMenuItemCount($h)
for ($i=0;$i -lt $n;$i++) {
  $rc=New-Object T+RECT; $ok=[T]::GetMenuItemRect([IntPtr]::Zero,$h,[uint32]$i,[ref]$rc)
  $pt=New-Object T+POINT; $pt.X=[int](($rc.L+$rc.R)/2); $pt.Y=[int](($rc.T+$rc.B)/2)
  $hit=[T]::WindowFromPhysicalPoint($pt); $root=[T]::GetAncestor($hit,2)
  $sb=New-Object System.Text.StringBuilder 128; [void][T]::GetMenuString($h,[uint32]$i,$sb,128,0x400)
  "  [$i] '$sb' rect=($($rc.L),$($rc.T),$($rc.R),$($rc.B)) centre=($($pt.X),$($pt.Y)) hit=0x{0:X} root=0x{1:X} rootIsMenu={2}" -f $hit.ToInt64(),$root.ToInt64(),($root -eq $m32)
}
Send-Escape; Close-Overflow
"foreground after Escape: " + [T]::D([T]::GetForegroundWindow())
```

- [ ] **Step 2: Run it** (`pwsh -NoProfile -File $env:TEMP\flaui-tray-probes\q9.ps1`). Expected if Q9 = yes: every item line ends `rootIsMenu=True`. If some are `False`, note which (a separator row may hit-test to the menu too; the question is whether the ROOT equals the `#32768` HWND).

- [ ] **Step 3: Record** in SPEC §8 row Q9: `MEASURED 2026-11-06 (<session>): WindowFromPhysicalPoint → GA_ROOT == #32768 for <n>/<n> items`. If it is not the menu window for any item, record what it is; Task 17 then passes that HWND as the expected root.

- [ ] **Step 4: Commit:** `git commit -am "docs(spec): B2 Q9 measured"`.

### Task 3: Q2 (`GetMenuItemRect` at 125 %)

SPEC §8 Q2. Needs a session at > 100 % scaling: on the reference machine that is **RDP at 125 %**. **Ask the operator before switching sessions** (standing rule).

- [ ] **Step 1: Ask the operator to connect over RDP** and confirm; then run `q9.ps1` again from that session (it prints the rects and hit-test roots; `common.ps1` sets per-monitor-v2 awareness so the rects are physical pixels of a DPI-aware caller). Also run it once **without** the `SetProcessDpiAwarenessContext` line (comment it out in a copy `q2-unaware.ps1`) to see what a DPI-unaware caller gets, because the server process's awareness is what matters: check `src/FlaUI.Mcp.Server/app.manifest` or `Program.cs` for the server's declared awareness (`grep -rn "dpi" src/FlaUI.Mcp.Server --include=*.manifest --include=*.cs --include=*.csproj`) and run the variant matching it.

- [ ] **Step 2: Decide.** Q2 = yes when, in the server's awareness mode, every item centre hit-tests to the `#32768` root (`rootIsMenu=True`) at 125 %. Record in SPEC §8 Q2: `MEASURED 2026-11-06 (RDP 125 %, server awareness=<mode>): rects physical=<yes/no>; hit-test root == menu for <n>/<n>`. If NO: Task 17 must scale the rect by the menu window's DPI (`GetDpiForWindow(hwnd) / 96.0`) and Task 20 adds a `ScaledDisplay` variant of the K3 selection test (SPEC §8 Q2 "Where" column).

- [ ] **Step 3: Commit** `docs(spec): B2 Q2 measured`, then ask the operator before returning to the console.

### Task 4: Q3 (auto-hidden taskbar) and Q8 (chevron rule with nothing hidden; second language)

SPEC §5 bullet 4, §8 Q3, Q8.

- [ ] **Step 1: Write `q3.ps1`** (sets auto-hide through the AppBar API, measures, restores in `finally`):

```powershell
. "$env:TEMP\flaui-tray-probes\common.ps1"
$ABM_GETSTATE=4; $ABM_SETSTATE=10; $ABS_AUTOHIDE=1
$d = New-Object T+APPBARDATA; $d.cbSize=[System.Runtime.InteropServices.Marshal]::SizeOf($d)
$orig = [uint64][T]::SHAppBarMessage($ABM_GETSTATE,[ref]$d); "original ABM state: $orig"
try {
  $d.lParam=[IntPtr]($orig -bor $ABS_AUTOHIDE); [void][T]::SHAppBarMessage($ABM_SETSTATE,[ref]$d); Start-Sleep -Seconds 2
  $p=New-Object T+POINT; [void][T]::GetCursorPos([ref]$p); [void][T]::SetCursorPos(400,400); Start-Sleep -Seconds 2   # move away so the bar hides
  $tray = Get-Tray; $r=$tray.Current.BoundingRectangle; "Shell_TrayWnd rect while hidden: $r"
  $icon = Find-Icon 'ShareX'; if ($icon) { $ir=$icon.Current.BoundingRectangle; "icon rect: $ir isOffscreen=$($icon.Current.IsOffscreen)"
    $pt=New-Object T+POINT; $pt.X=[int]($ir.X+$ir.Width/2); $pt.Y=[int]($ir.Y+$ir.Height/2)
    "hit-test at icon centre: " + [T]::D([T]::GetAncestor([T]::WindowFromPhysicalPoint($pt),2)) }
  "chevron found: " + [bool](Get-Chevron)
  [void][T]::SetCursorPos($p.X,$p.Y)
} finally { $d.lParam=[IntPtr]$orig; [void][T]::SHAppBarMessage($ABM_SETSTATE,[ref]$d); "restored ABM state: " + [uint64][T]::SHAppBarMessage($ABM_GETSTATE,[ref]$d) }
```

- [ ] **Step 2: Run it** on the console. Record in SPEC §8 Q3 what the icon's rect, `IsOffscreen` and the hit-test root are while hidden. The product rule (Task 16) is: before clicking, if `IsOffscreen` is true **or** the hit-test root at the icon centre is not `Shell_TrayWnd`/the overflow, fail `ElementNotActionable` ("the taskbar is hidden; the icon is not on screen"). Write in the cell whether the measurement matched that expectation. If `SHAppBarMessage` did not change the state (`restored` ≠ `original|1` during the run), say so and record Q3 as `UNVERIFIED-ACCEPTED: auto-hide could not be toggled programmatically; the §5 rule stands as expected behaviour`.

- [ ] **Step 3: Q8, nothing hidden.** Run in PowerShell: `. "$env:TEMP\flaui-tray-probes\common.ps1"; (Get-Tray).FindAll('Descendants',(New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty,'SystemTrayIcon'))) | % { "$($_.Current.Name) | $($_.Current.ClassName)" }`. Record the list. Then the "nothing hidden" case: Settings → Personalization → Taskbar → "Other system tray icons" → turn on every icon (operator does this, or skip), re-run, and record whether a `SystemTray.NormalButton` candidate still exists. If the operator declines, record `UNVERIFIED-ACCEPTED: nothing-hidden state not arranged; rule falls back to "no candidate ⇒ hidden list empty"`.

- [ ] **Step 4: Q8, second language.** No second-language machine is available on 2026-11-06. Record `UNVERIFIED-ACCEPTED: no second display language available; the rule never matches on Name, only on aid+class+effect`.

- [ ] **Step 5: Commit** `docs(spec): B2 Q3 Q8 measured`.

---

## Part B — the tray fixture, then the measurements that need it (Q4, Q5, Q6, Q7)

### Task 5: `test/FlaUI.Mcp.TrayFixture` (WinForms app with a tray icon)

SPEC §7 "Fixture". One app, three kinds. It never shows a taskbar window of its own (so `desktop_list_windows` stays clean), logs every command, and exits on a registered message.

**Files:**
- Create: `test/FlaUI.Mcp.TrayFixture/FlaUI.Mcp.TrayFixture.csproj`
- Create: `test/FlaUI.Mcp.TrayFixture/Program.cs`
- Create: `test/FlaUI.Mcp.TrayFixture/TrayForm.cs`
- Modify: `FlaUI.Mcp.slnx` (add the project under `/test/`)

- [ ] **Step 1: csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: `Program.cs`** (license header first):

```csharp
using FlaUI.Mcp.TrayFixture;

// Arguments: --kind uia|classic|window   --tooltip "<text>"   (SPEC §7)
string kind = "uia";
string tooltip = $"FlaUI Tray Fixture {Guid.NewGuid():N}";
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--kind") kind = args[i + 1];
    else if (args[i] == "--tooltip") tooltip = args[i + 1];
}
if (kind is not ("uia" or "classic" or "window"))
{
    Console.Error.WriteLine($"unknown --kind '{kind}' (uia|classic|window)");
    return 2;
}
ApplicationConfiguration.Initialize();
Application.Run(new TrayForm(kind, tooltip));
return 0;
```

- [ ] **Step 3: `TrayForm.cs`** (license header first):

```csharp
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.TrayFixture;

/// <summary>A hidden form that owns one tray icon. kind=uia: WinForms ContextMenuStrip (K1).
/// kind=classic: TrackPopupMenu (K3). kind=window: a titled form (K2). Every command is appended to
/// %TEMP%\flaui-trayfixture-&lt;pid&gt;.log so a test asserts what the app RECEIVED, not only what the
/// tool replied. The icon is removed on a registered exit message, so a killed fixture never leaves a
/// ghost icon behind (SPEC §7, F11).</summary>
public sealed class TrayForm : Form
{
    public const string ExitMessageName = "FlaUI.Mcp.TrayFixture.Exit";
    private static readonly uint ExitMsg = RegisterWindowMessage(ExitMessageName);
    public static string LogPath(int pid) => Path.Combine(Path.GetTempPath(), $"flaui-trayfixture-{pid}.log");

    // Menu items are the same for every kind: one enabled, one disabled, one submenu, a separator, two more.
    private const int IdAlpha = 101, IdDisabled = 102, IdSub = 103, IdBeta = 104, IdExit = 105;

    private readonly NotifyIcon _icon;
    private readonly bool _allowVisible; // always false: the form is a message sink, never shown

    public TrayForm(string kind, string tooltip)
    {
        Text = $"FlaUI.Mcp.TrayFixture {Environment.ProcessId}";
        ShowInTaskbar = false;
        _ = Handle; // create the HWND now so the exit message has somewhere to go while the form stays hidden
        _icon = new NotifyIcon { Icon = SystemIcons.Application, Text = tooltip, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Log("left-click"); };
        if (kind == "uia")
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Alpha", null, (_, _) => Log("command Alpha"));
            menu.Items.Add(new ToolStripMenuItem("Disabled Item") { Enabled = false });
            var more = new ToolStripMenuItem("More");
            more.DropDownItems.Add("Sub Item", null, (_, _) => Log("command Sub Item"));
            menu.Items.Add(more);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Beta", null, (_, _) => Log("command Beta"));
            menu.Items.Add("Exit", null, (_, _) => ExitNow());
            menu.Opened += (_, _) => Log("menu opened");
            menu.Closed += (_, _) => Log("menu closed");
            _icon.ContextMenuStrip = menu;
        }
        else
        {
            _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) { if (kind == "classic") ShowClassicMenu(); else ShowWindow(); } };
        }
        Log($"started kind={kind} hwnd=0x{Handle.ToInt64():X} tooltip={tooltip}");
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(_allowVisible && value);

    private void ShowClassicMenu()
    {
        var h = CreatePopupMenu();
        var sub = CreatePopupMenu();
        AppendMenu(sub, MF_STRING, IdSub, "Sub Item");
        AppendMenu(h, MF_STRING, IdAlpha, "&Alpha");
        AppendMenu(h, MF_STRING | MF_GRAYED, IdDisabled, "Disabled Item");
        AppendMenu(h, MF_POPUP, sub, "More");
        AppendMenu(h, MF_SEPARATOR, 0, null);
        AppendMenu(h, MF_STRING, IdBeta, "Beta\tCtrl+B");
        AppendMenu(h, MF_STRING, IdExit, "Exit");
        GetCursorPos(out var p);
        SetForegroundWindow(Handle); // required by TrackPopupMenu for a correct dismissal
        Log("menu opened");
        int cmd = TrackPopupMenuEx(h, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, Handle, IntPtr.Zero);
        PostMessage(Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(h);
        Log("menu closed");
        switch (cmd)
        {
            case IdAlpha: Log("command Alpha"); break;
            case IdSub: Log("command Sub Item"); break;
            case IdBeta: Log("command Beta"); break;
            case IdExit: ExitNow(); break;
            case 0: break; // dismissed
            default: Log($"command id={cmd}"); break;
        }
    }

    private void ShowWindow()
    {
        var f = new Form { Text = "FlaUI Tray Fixture Window", Width = 320, Height = 200, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        var b = new Button { Text = "Alpha", Left = 20, Top = 20, Width = 120 };
        b.Click += (_, _) => { Log("command Alpha"); f.Close(); };
        f.Controls.Add(b);
        f.KeyPreview = true;
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.FormClosed += (_, _) => Log("window closed");
        Log("window opened");
        f.Show();
        f.Activate();
    }

    private void ExitNow()
    {
        Log("exit");
        _icon.Visible = false;
        _icon.Dispose();
        Application.Exit();
    }

    protected override void WndProc(ref Message m)
    {
        if (ExitMsg != 0 && (uint)m.Msg == ExitMsg) { ExitNow(); return; }
        base.WndProc(ref m);
    }

    private static void Log(string what)
    {
        try { File.AppendAllText(LogPath(Environment.ProcessId), $"{DateTime.UtcNow:HH:mm:ss.fff} {what}{Environment.NewLine}"); }
        catch { /* diagnostics only */ }
    }

    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    private const uint WM_NULL = 0;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hMenu, uint flags, nint idOrSubmenu, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
```

- [ ] **Step 4: Add to `FlaUI.Mcp.slnx`** after the TestApp line: `    <Project Path="test/FlaUI.Mcp.TrayFixture/FlaUI.Mcp.TrayFixture.csproj" />`.

- [ ] **Step 5: Build:** `dotnet build FlaUI.Mcp.slnx -c Debug` → `0 Warning(s)`, `0 Error(s)`. Fix any CS warning (they are errors) without disabling them.

- [ ] **Step 6: Smoke by hand (console).** Start it: `Start-Process test\FlaUI.Mcp.TrayFixture\bin\Debug\net10.0-windows\FlaUI.Mcp.TrayFixture.exe -ArgumentList '--kind','uia','--tooltip','"FlaUI Tray Fixture smoke"'`. Confirm the icon: `. "$env:TEMP\flaui-tray-probes\common.ps1"; [bool](Find-Icon 'FlaUI Tray Fixture' -Overflow); Close-Overflow` → `True`, and the log `$env:TEMP\flaui-trayfixture-<pid>.log` holds the `started` line. Stop it with the exit message (`FindWindow(null, "FlaUI.Mcp.TrayFixture <pid>")` then `PostMessage(hwnd, RegisterWindowMessage("FlaUI.Mcp.TrayFixture.Exit"), 0, 0)`; the `TrayFixtureHost` in Task 6 does exactly this) → the icon disappears and the log ends with `exit`.

- [ ] **Step 7: Commit:** `git add test/FlaUI.Mcp.TrayFixture FlaUI.Mcp.slnx && git commit -m "test(tray): add FlaUI.Mcp.TrayFixture (K1/K2/K3 tray icon fixture)"`.

### Task 6: `TrayFixtureHost` test helper

**Files:** Create `test/FlaUI.Mcp.Tests/Tray/TrayFixtureHost.cs`, create `test/FlaUI.Mcp.Tests/Tray/TrayDesktopTests.cs`.

- [ ] **Step 1: Write the host** (mirrors `TestAppFixture.cs` path resolution):

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Tests.Tray;

/// <summary>Launches test/FlaUI.Mcp.TrayFixture with a unique tooltip and stops it GRACEFULLY (registered
/// exit message → NotifyIcon removed). Kill is the last resort only: a killed fixture leaves a ghost icon
/// that a later test could match (SPEC §7, F11).</summary>
public sealed class TrayFixtureHost : IDisposable
{
    public Process Process { get; }
    public string Tooltip { get; }
    public string Kind { get; }
    public string LogPath => Path.Combine(Path.GetTempPath(), $"flaui-trayfixture-{Process.Id}.log");

    public TrayFixtureHost(string kind, string? tooltipPrefix = null)
    {
        Kind = kind;
        Tooltip = $"{tooltipPrefix ?? "FlaUI Tray Fixture"} {Guid.NewGuid():N}";
        var root = AppContext.BaseDirectory;
        string Candidate(string config) => Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..",
            "test", "FlaUI.Mcp.TrayFixture", "bin", config, "net10.0-windows", "FlaUI.Mcp.TrayFixture.exe"));
        var thisConfig = new DirectoryInfo(root.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";
        var exe = new[] { thisConfig, "Release", "Debug" }.Select(Candidate).FirstOrDefault(File.Exists)
                  ?? throw new FileNotFoundException($"TrayFixture not built. Run: dotnet build -c {thisConfig} test/FlaUI.Mcp.TrayFixture");
        Process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = $"--kind {kind} --tooltip \"{Tooltip}\"" })!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !Events().Any(e => e.StartsWith("started ")))
        {
            if (Process.HasExited) throw new InvalidOperationException($"TrayFixture exited early (code {Process.ExitCode})");
            Thread.Sleep(50);
        }
        if (!Events().Any(e => e.StartsWith("started "))) throw new TimeoutException("TrayFixture did not log 'started' within 10 s");
        Thread.Sleep(500); // let explorer place the icon
    }

    public string[] Lines()
    {
        try { return File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    /// <summary>Lines after the 12-char timestamp + space, e.g. "command Alpha", "left-click", "menu opened".</summary>
    public string[] Events() => Lines().Select(l => l.Length > 13 ? l[13..] : l).ToArray();

    public bool WaitForEvent(string text, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline) { if (Events().Contains(text)) return true; Thread.Sleep(50); }
        return false;
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                var hwnd = FindWindowW(null, $"FlaUI.Mcp.TrayFixture {Process.Id}");
                if (hwnd != IntPtr.Zero) PostMessageW(hwnd, RegisterWindowMessageW("FlaUI.Mcp.TrayFixture.Exit"), IntPtr.Zero, IntPtr.Zero);
                if (!Process.WaitForExit(3000)) { Process.Kill(entireProcessTree: true); Process.WaitForExit(3000); }
            }
        }
        catch { /* already gone */ }
        try { File.Delete(LogPath); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
```

- [ ] **Step 2: Write the Desktop smoke test** (the file grows in Task 19; create it with this one test):

```csharp
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

[Trait("Category", "Desktop")]
public class TrayDesktopTests
{
    [Fact]
    public void Fixture_starts_logs_and_exits_gracefully()
    {
        string log;
        using (var fx = new TrayFixtureHost("uia"))
        {
            log = fx.LogPath;
            Assert.Contains(fx.Events(), e => e.StartsWith("started kind=uia"));
        }
        Assert.False(File.Exists(log)); // Dispose deleted it after a graceful exit
    }
}
```

- [ ] **Step 3: Run:** `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~TrayDesktopTests"` → `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 4: Commit:** `git add test/FlaUI.Mcp.Tests/Tray && git commit -m "test(tray): TrayFixtureHost helper and fixture lifecycle smoke test"`.

### Task 7: Q5, Q4, Q6 with the fixture (console)

- [ ] **Step 1: Q5 (does a new icon land in the overflow?).** Start the fixture by hand (`--kind uia --tooltip "FlaUI Tray Fixture q5"`), then: `. "$env:TEMP\flaui-tray-probes\common.ps1"; "visible: " + [bool](Find-Icon 'FlaUI Tray Fixture q5'); "hidden: " + [bool](Find-Icon 'FlaUI Tray Fixture q5' -Overflow); Close-Overflow`. Record in SPEC §8 Q5: `MEASURED 2026-11-06: new icon location = hidden|visible`. If **visible**, SPEC §7's hidden-path tests must instead demote the icon (Task 8's Q7 answers whether that is possible) and the Task 19/20 `location` assertions flip accordingly; record that consequence in the cell.

- [ ] **Step 2: Q4 (TrackPopupMenu fixture classifies as K3?).** Start `--kind classic --tooltip "FlaUI Tray Fixture q4"`; run `q9.ps1 -IconName 'FlaUI Tray Fixture q4'` (with `-Overflow` per Q5). Expected: a `#32768` HWND, `MN_GETHMENU` non-zero, 6 items (`&Alpha`, `Disabled Item`, `More`, separator, `Beta<tab>Ctrl+B`, `Exit`) with rects, and the UIA root a Pane with zero `MenuItem`s (add to the probe: `($A::FromHandle($m32)).FindAll('Descendants',(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem))).Count`). Record Q4 with the counts. Stop the fixture via the exit message.

- [ ] **Step 3: Q6 (right-click result's process == foreground process?).** Save as `q6.ps1`; for each kind (`uia`, `classic`, `window`) start a fixture and run it:

```powershell
param([string]$IconName, [switch]$Overflow)
. "$env:TEMP\flaui-tray-probes\common.ps1"
$icon = Find-Icon $IconName -Overflow:$Overflow; $before=[T]::Visible()
Invoke-RightClick $icon
$fg=[T]::GetForegroundWindow(); $fgPid=0; [void][T]::GetWindowThreadProcessId($fg,[ref]$fgPid)
"foreground: " + [T]::D($fg)
@([T]::Visible() | ? { $before -notcontains $_ }) | % { $p=0; [void][T]::GetWindowThreadProcessId($_,[ref]$p); "new: " + [T]::D($_) + " sameProcessAsForeground=$($p -eq $fgPid)" }
Send-Escape; Close-Overflow
```

Expected: for every kind the new K1/K3/K2 window reports `sameProcessAsForeground=True` (explorer's `Xaml_WindowedPopupClass` PopupHost may appear too; §2 step 4 ignores it). Record Q6 per kind. If a kind's result window is NOT in the foreground process, record it and **change SPEC §2 step 5**: the candidate rule becomes "foreground process, OR owned (GW_OWNER chain) by a window of the foreground process"; Task 13's `CandidateFacts` then gains `OwnerPid`.

- [ ] **Step 4: Commit** `docs(spec): B2 Q4 Q5 Q6 measured`.

### Task 8: Q7 (promote the fixture icon to the visible tray)

SPEC §8 Q7. Windows 11 keeps per-icon settings under `HKCU\Control Panel\NotifyIconSettings\<id>` with `ExecutablePath` and `IsPromoted` (DWORD).

- [ ] **Step 1: Probe `q7.ps1`:**

```powershell
param([string]$Tooltip='FlaUI Tray Fixture q7')
. "$env:TEMP\flaui-tray-probes\common.ps1"
$exe = (Resolve-Path 'test\FlaUI.Mcp.TrayFixture\bin\Debug\net10.0-windows\FlaUI.Mcp.TrayFixture.exe').Path
$p = Start-Process $exe -ArgumentList "--kind uia --tooltip `"$Tooltip`"" -PassThru; Start-Sleep 2
"before: visible=$([bool](Find-Icon $Tooltip)) hidden=$([bool](Find-Icon $Tooltip -Overflow))"; Close-Overflow
$keys = Get-ChildItem 'HKCU:\Control Panel\NotifyIconSettings' | ? { (Get-ItemProperty $_.PSPath).ExecutablePath -like '*TrayFixture*' }
"registry keys for the fixture: $($keys.Count)"; $keys | % { "  $($_.PSChildName) IsPromoted=$((Get-ItemProperty $_.PSPath).IsPromoted)" }
if ($keys.Count -eq 0) { "no key found; Q7 = cannot promote by registry"; Stop-Process -Id $p.Id; exit }
$k = $keys[0]; $orig = (Get-ItemProperty $k.PSPath).IsPromoted
Set-ItemProperty $k.PSPath -Name IsPromoted -Value 1 -Type DWord
Start-Sleep 2; "after IsPromoted=1 (running): visible=$([bool](Find-Icon $Tooltip))"
Stop-Process -Id $p.Id; Start-Sleep 1
$p = Start-Process $exe -ArgumentList "--kind uia --tooltip `"$Tooltip`"" -PassThru; Start-Sleep 2
"after restart: visible=$([bool](Find-Icon $Tooltip)) hidden=$([bool](Find-Icon $Tooltip -Overflow))"; Close-Overflow
if ($null -eq $orig) { Remove-ItemProperty $k.PSPath -Name IsPromoted } else { Set-ItemProperty $k.PSPath -Name IsPromoted -Value $orig -Type DWord }
Stop-Process -Id $p.Id
"restored IsPromoted to: $orig"
```

(The probe kills the fixture, so a ghost icon may remain for this run only; click it once in the tray afterwards and explorer discards it.)

- [ ] **Step 2: Run it** and record Q7: `MEASURED 2026-11-06: key <found/not found>; IsPromoted=1 takes effect <live / after restart / never>`. If promotion works, Task 20's visible-icon right-click test promotes via the registry in its arrange (same key lookup by `ExecutablePath`), restores in `finally`, and restarts the fixture if "after restart" was the answer. If it never works, record `UNVERIFIED-ACCEPTED: the visible-path right-click is not testable with the fixture; covered manually on ShareX (Q1)` under SPEC "Stand-downs" too.

- [ ] **Step 3: Commit** `docs(spec): B2 Q7 measured`.

---

## Part C — pure decision units (headless, TDD)

Every file in this part is in `src/FlaUI.Mcp.Core/Tray/` (namespace `FlaUI.Mcp.Core.Tray`) with its test in `test/FlaUI.Mcp.Tests/Tray/`. None of them touches UIA or Win32, so the headless gate pins them in CI.

### Task 9: `MenuLabel.Clean`

SPEC §2 step 7: drop single `&` accelerator markers, `&&` → `&`, drop from the first tab, trim.

**Files:** Create `src/FlaUI.Mcp.Core/Tray/MenuLabel.cs`, `test/FlaUI.Mcp.Tests/Tray/MenuLabelTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class MenuLabelTests
{
    [Theory]
    [InlineData("&Alpha", "Alpha")]
    [InlineData("E&xit", "Exit")]
    [InlineData("Save && Exit", "Save & Exit")]
    [InlineData("Beta\tCtrl+B", "Beta")]
    [InlineData("&Open\tCtrl+O", "Open")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", "")]
    [InlineData("&&", "&")]
    [InlineData("trailing&", "trailing")]
    public void Clean_strips_accelerators_shortcut_text_and_whitespace(string raw, string expected)
        => Assert.Equal(expected, MenuLabel.Clean(raw));

    [Fact]
    public void Clean_of_null_is_empty() => Assert.Equal("", MenuLabel.Clean(null));
}
```

- [ ] **Step 2: Run** `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~MenuLabelTests"` → build error `MenuLabel` not found.

- [ ] **Step 3: Implement**

```csharp
using System.Text;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>Win32/WinForms menu label → the text an agent would read on screen (SPEC §2 step 7):
/// `&amp;&amp;` is a literal ampersand, a single `&amp;` marks the accelerator and is dropped, everything
/// from the first TAB is shortcut text, and the result is trimmed.</summary>
public static class MenuLabel
{
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int tab = raw.IndexOf('\t');
        if (tab >= 0) raw = raw[..tab];
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '&') { sb.Append(raw[i]); continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '&') { sb.Append('&'); i++; }
            // a single '&' is the accelerator marker: dropped
        }
        return sb.ToString().Trim();
    }
}
```

- [ ] **Step 4: Run** the same filter → `Passed! - Failed: 0, Passed: 10`.

- [ ] **Step 5: Commit** `git add src/FlaUI.Mcp.Core/Tray/MenuLabel.cs test/FlaUI.Mcp.Tests/Tray/MenuLabelTests.cs && git commit -m "feat(tray): MenuLabel.Clean (accelerators, shortcut text)"`.

### Task 10: `TrayMatch` (icon substring match; item equality-then-substring match)

SPEC §1.3 (`icon`: case-insensitive substring; 0 → `SelectorNoMatch`, >1 → `AmbiguousMatch`) and §2 step 7 (`menuItem`: exact equality first, then substring).

**Files:** Create `src/FlaUI.Mcp.Core/Tray/TrayMatch.cs`, `test/FlaUI.Mcp.Tests/Tray/TrayMatchTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayMatchTests
{
    private static readonly string[] Tooltips = { "Docker Desktop\nRunning", "OneDrive - Personal", "ShareX", "Docker Compose helper" };

    [Fact]
    public void Icon_substring_is_case_insensitive_and_unique()
    {
        var r = TrayMatch.IconBySubstring(Tooltips, "sharex");
        Assert.Equal(MatchKind.Unique, r.Kind);
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void Icon_substring_with_two_hits_is_ambiguous_and_lists_both()
    {
        var r = TrayMatch.IconBySubstring(Tooltips, "docker");
        Assert.Equal(MatchKind.Ambiguous, r.Kind);
        Assert.Equal(new[] { 0, 3 }, r.Candidates);
    }

    [Fact]
    public void Icon_substring_with_no_hit_is_none()
        => Assert.Equal(MatchKind.None, TrayMatch.IconBySubstring(Tooltips, "vpn").Kind);

    [Fact]
    public void Item_exact_match_wins_over_substring_matches()
    {
        var r = TrayMatch.ItemByText(new[] { "Quit", "Quit Docker Desktop", "Restart" }, "quit");
        Assert.Equal(MatchKind.Unique, r.Kind);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Item_falls_back_to_substring_when_nothing_is_exact()
    {
        var r = TrayMatch.ItemByText(new[] { "Open main window", "Exit" }, "main");
        Assert.Equal(MatchKind.Unique, r.Kind);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Item_two_substring_hits_and_no_exact_is_ambiguous()
    {
        var r = TrayMatch.ItemByText(new[] { "Pause sync", "Resume sync", "Exit" }, "sync");
        Assert.Equal(MatchKind.Ambiguous, r.Kind);
        Assert.Equal(new[] { 0, 1 }, r.Candidates);
    }

    [Fact]
    public void Item_two_identical_exact_texts_is_ambiguous()
        => Assert.Equal(MatchKind.Ambiguous, TrayMatch.ItemByText(new[] { "Open", "Open" }, "open").Kind);

    [Fact]
    public void Item_no_hit_is_none()
        => Assert.Equal(MatchKind.None, TrayMatch.ItemByText(new[] { "Exit" }, "quit").Kind);
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TrayMatchTests"` → build error.

- [ ] **Step 3: Implement**

```csharp
namespace FlaUI.Mcp.Core.Tray;

public enum MatchKind { None, Unique, Ambiguous }

/// <summary>Outcome of matching a caller's text against a list. Index is valid only for Unique;
/// Candidates lists every hit (empty for None, one for Unique, all for Ambiguous).</summary>
public readonly record struct MatchResult(MatchKind Kind, int Index, IReadOnlyList<int> Candidates);

/// <summary>Pure matching rules (SPEC §1.3, §2 step 7). Callers validate that the query is not
/// blank BEFORE calling (an empty substring would match everything).</summary>
public static class TrayMatch
{
    /// <summary>`icon` rule: case-insensitive substring over the tooltips.</summary>
    public static MatchResult IconBySubstring(IReadOnlyList<string> tooltips, string query)
        => FromHits(Hits(tooltips, t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));

    /// <summary>`menuItem` rule: exact (case-insensitive) equality first; only when nothing is equal,
    /// case-insensitive substring.</summary>
    public static MatchResult ItemByText(IReadOnlyList<string> cleanedTexts, string query)
    {
        var exact = Hits(cleanedTexts, t => string.Equals(t, query, StringComparison.OrdinalIgnoreCase));
        if (exact.Count > 0) return FromHits(exact);
        return FromHits(Hits(cleanedTexts, t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<int> Hits(IReadOnlyList<string> items, Func<string, bool> pred)
    {
        var hits = new List<int>();
        for (int i = 0; i < items.Count; i++) if (pred(items[i] ?? string.Empty)) hits.Add(i);
        return hits;
    }

    private static MatchResult FromHits(List<int> hits) => hits.Count switch
    {
        0 => new MatchResult(MatchKind.None, -1, hits),
        1 => new MatchResult(MatchKind.Unique, hits[0], hits),
        _ => new MatchResult(MatchKind.Ambiguous, -1, hits),
    };
}
```

- [ ] **Step 4: Run** → `Passed: 8`.

- [ ] **Step 5: Commit** `git add src/FlaUI.Mcp.Core/Tray/TrayMatch.cs test/FlaUI.Mcp.Tests/Tray/TrayMatchTests.cs && git commit -m "feat(tray): TrayMatch (icon substring, item exact-then-substring)"`.

### Task 11: `TrayMenuClassifier` (K1/K2/K3 from window facts; stability; strongest wins)

SPEC §2 steps 4–5. Pure over a `CandidateFacts` record the orchestrator fills from Win32/UIA.

**Files:** Create `src/FlaUI.Mcp.Core/Tray/MenuKind.cs`, `test/FlaUI.Mcp.Tests/Tray/TrayMenuClassifierTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayMenuClassifierTests
{
    private static CandidateFacts Facts(string cls = "WindowsForms10.Window.20808", bool hasTitle = false, int pid = 42,
        bool isForeground = false, bool hasHmenu = false, bool uiaRootIsMenu = false, bool actionableItems = false)
        => new(Hwnd: 0x1000, ClassName: cls, HasTitle: hasTitle, Pid: pid, IsForeground: isForeground,
               HasHmenu: hasHmenu, UiaRootIsMenu: uiaRootIsMenu, HasActionableMenuItems: actionableItems);

    [Fact] // M13 ZoomIt
    public void Classic_32768_with_hmenu_is_K3()
        => Assert.Equal(MenuKind.ClassicMenu, TrayMenuClassifier.Classify(Facts(cls: "#32768", hasHmenu: true), foregroundPid: 42));

    [Fact] // M6 ShareX
    public void Untitled_window_whose_uia_root_is_Menu_is_K1()
        => Assert.Equal(MenuKind.UiaMenu, TrayMenuClassifier.Classify(Facts(uiaRootIsMenu: true), 42));

    [Fact] // panel r2: WPF/Qt popup host rooted as Pane with invokable MenuItems
    public void Untitled_window_with_actionable_menu_items_is_K1()
        => Assert.Equal(MenuKind.UiaMenu, TrayMenuClassifier.Classify(Facts(actionableItems: true), 42));

    [Fact] // M7 OneDrive: titled, contains MenuItems (Value only) — must NOT be K1
    public void Titled_foreground_window_is_K2_even_with_menu_items()
        => Assert.Equal(MenuKind.AppWindow, TrayMenuClassifier.Classify(Facts(hasTitle: true, isForeground: true, actionableItems: true), 42));

    [Fact]
    public void Titled_window_that_is_not_foreground_is_not_a_candidate()
        => Assert.Equal(MenuKind.None, TrayMenuClassifier.Classify(Facts(hasTitle: true, isForeground: false), 42));

    [Fact] // F6
    public void Window_of_another_process_than_the_foreground_is_never_a_candidate()
        => Assert.Equal(MenuKind.None, TrayMenuClassifier.Classify(Facts(cls: "#32768", hasHmenu: true, pid: 7), foregroundPid: 42));

    [Fact]
    public void Untitled_window_with_no_menu_evidence_is_none()
        => Assert.Equal(MenuKind.None, TrayMenuClassifier.Classify(Facts(), 42));

    [Theory]
    [InlineData("Xaml_WindowedPopupClass")]
    [InlineData("SysShadow")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    public void Explorer_tooltip_shadow_and_overflow_windows_are_ignored(string cls)
        => Assert.True(TrayMenuClassifier.IsIgnoredClass(cls));

    [Fact]
    public void The_intent_overlay_sentinel_is_ignored()
        => Assert.True(TrayMenuClassifier.IsIgnoredClass(FlaUI.Mcp.Core.Interaction.OverlaySentinel.ClassName));

    [Fact]
    public void Strongest_prefers_K3_then_K1_then_K2()
    {
        var picked = TrayMenuClassifier.Strongest(new[]
        {
            new ClassifiedWindow(0x1, MenuKind.AppWindow, 0),
            new ClassifiedWindow(0x2, MenuKind.UiaMenu, 5),
            new ClassifiedWindow(0x3, MenuKind.ClassicMenu, 4),
        });
        Assert.Equal((nint)0x3, picked!.Value.Hwnd);
    }

    [Fact]
    public void Strongest_of_nothing_is_null()
        => Assert.Null(TrayMenuClassifier.Strongest(Array.Empty<ClassifiedWindow>()));

    [Fact] // F1: two consecutive polls, same non-empty set, same item counts
    public void Picture_is_stable_only_when_non_empty_and_identical_to_the_previous_poll()
    {
        var a = new[] { new ClassifiedWindow(0x2, MenuKind.UiaMenu, 5) };
        var b = new[] { new ClassifiedWindow(0x2, MenuKind.UiaMenu, 5) };
        var c = new[] { new ClassifiedWindow(0x2, MenuKind.UiaMenu, 6) };
        Assert.True(TrayMenuClassifier.IsStable(a, b));
        Assert.False(TrayMenuClassifier.IsStable(a, c));
        Assert.False(TrayMenuClassifier.IsStable(Array.Empty<ClassifiedWindow>(), Array.Empty<ClassifiedWindow>()));
        Assert.False(TrayMenuClassifier.IsStable(null, a));
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TrayMenuClassifierTests"` → build error.

- [ ] **Step 3: Implement** `MenuKind.cs`:

```csharp
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>What a right-click produced (SPEC "Three kinds of right-click result"). On the wire
/// ClassicMenu and UiaMenu are both `menuKind: "menu"`; AppWindow is `menuKind: "window"`.</summary>
public enum MenuKind { None, ClassicMenu, UiaMenu, AppWindow }

/// <summary>Everything the classifier needs about one new top-level window, read by the orchestrator
/// (Win32 for class/title/pid/foreground/hmenu; UIA for the two menu facts, on the action STA).</summary>
public sealed record CandidateFacts(
    nint Hwnd, string ClassName, bool HasTitle, int Pid, bool IsForeground,
    bool HasHmenu, bool UiaRootIsMenu, bool HasActionableMenuItems);

/// <summary>One poll's view of a candidate: its kind and how many items it currently exposes.</summary>
public readonly record struct ClassifiedWindow(nint Hwnd, MenuKind Kind, int ItemCount);

/// <summary>SPEC §2 steps 4–5, pure.</summary>
public static class TrayMenuClassifier
{
    public const string ClassicMenuClass = "#32768";
    public const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";
    public const string TaskbarClass = "Shell_TrayWnd";

    /// <summary>Windows the poll never considers: explorer's tooltip/popup host, menu shadows, the
    /// overflow itself, and the intent overlay (`WindowManager.ShouldSkipTopLevel`).</summary>
    public static bool IsIgnoredClass(string className)
        => className is "Xaml_WindowedPopupClass" or "SysShadow" or OverflowClass
           || WindowManager.ShouldSkipTopLevel(className);

    /// <summary>K3 > K1 > K2, each gated by the facts the spec names. Only a window of the foreground
    /// process is a candidate at all (F6).</summary>
    public static MenuKind Classify(CandidateFacts f, int foregroundPid)
    {
        if (f.Pid != foregroundPid) return MenuKind.None;
        if (f.ClassName == ClassicMenuClass && f.HasHmenu) return MenuKind.ClassicMenu;
        if (!f.HasTitle && (f.UiaRootIsMenu || f.HasActionableMenuItems)) return MenuKind.UiaMenu;
        if (f.HasTitle && f.IsForeground) return MenuKind.AppWindow;
        return MenuKind.None;
    }

    private static int Rank(MenuKind k) => k switch
    {
        MenuKind.ClassicMenu => 3, MenuKind.UiaMenu => 2, MenuKind.AppWindow => 1, _ => 0,
    };

    public static ClassifiedWindow? Strongest(IEnumerable<ClassifiedWindow> candidates)
    {
        ClassifiedWindow? best = null;
        foreach (var c in candidates)
            if (c.Kind != MenuKind.None && (best is null || Rank(c.Kind) > Rank(best.Value.Kind))) best = c;
        return best;
    }

    /// <summary>F1: stable = the previous poll exists, both are non-empty, and they hold the same
    /// (hwnd, kind, itemCount) set.</summary>
    public static bool IsStable(IReadOnlyList<ClassifiedWindow>? previous, IReadOnlyList<ClassifiedWindow> current)
    {
        if (previous is null || previous.Count == 0 || current.Count == 0 || previous.Count != current.Count) return false;
        var prev = previous.OrderBy(w => w.Hwnd).ToArray();
        var cur = current.OrderBy(w => w.Hwnd).ToArray();
        for (int i = 0; i < prev.Length; i++) if (prev[i] != cur[i]) return false;
        return true;
    }
}
```

- [ ] **Step 4: Run** → `Passed: 15`.

- [ ] **Step 5: Commit** `git add src/FlaUI.Mcp.Core/Tray/MenuKind.cs test/FlaUI.Mcp.Tests/Tray/TrayMenuClassifierTests.cs && git commit -m "feat(tray): TrayMenuClassifier (K1/K2/K3, ignore list, stability, strongest)"`.

### Task 12: `OverflowRestore` decision

SPEC §1.2 (list: close in `finally` if the tool opened it; leave if found open) and §1.3 "Overflow after an action" (close if the tool opened it and it is still open; never re-open; `overflowRestored` present whenever the call opened or found it open, `true` when end state == start state).

**Files:** Create `src/FlaUI.Mcp.Core/Tray/OverflowRestore.cs`, `test/FlaUI.Mcp.Tests/Tray/OverflowRestoreTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class OverflowRestoreTests
{
    [Fact]
    public void Tool_opened_it_and_it_is_still_open_closes_and_reports_restored()
    {
        var p = OverflowRestore.Decide(wasOpenAtStart: false, openedByTool: true, isOpenNow: true);
        Assert.True(p.CloseNow);
        Assert.True(p.Restored);
    }

    [Fact]
    public void Tool_opened_it_and_the_app_already_closed_it_reports_restored_without_closing()
    {
        var p = OverflowRestore.Decide(false, true, isOpenNow: false);
        Assert.False(p.CloseNow);
        Assert.True(p.Restored);
    }

    [Fact]
    public void Found_open_and_still_open_leaves_it_and_reports_restored()
    {
        var p = OverflowRestore.Decide(wasOpenAtStart: true, openedByTool: false, isOpenNow: true);
        Assert.False(p.CloseNow);
        Assert.True(p.Restored);
    }

    [Fact] // F4: never re-open over the app the action just raised
    public void Found_open_and_the_action_closed_it_is_not_reopened_and_reports_not_restored()
    {
        var p = OverflowRestore.Decide(true, false, isOpenNow: false);
        Assert.False(p.CloseNow);
        Assert.False(p.Restored);
    }

    [Fact]
    public void Never_touched_reports_nothing()
    {
        var p = OverflowRestore.Decide(wasOpenAtStart: false, openedByTool: false, isOpenNow: false);
        Assert.False(p.CloseNow);
        Assert.Null(p.Restored);
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~OverflowRestoreTests"` → build error.

- [ ] **Step 3: Implement**

```csharp
namespace FlaUI.Mcp.Core.Tray;

/// <summary>CloseNow: invoke the chevron now. Restored: the `overflowRestored` field (null = absent on the wire).</summary>
public readonly record struct OverflowPlan(bool CloseNow, bool? Restored);

/// <summary>SPEC §1.2 / §1.3 "Overflow after an action", pure. The tool closes the overflow only if IT
/// opened it; it never re-opens one that an action closed; it reports `overflowRestored` whenever it
/// opened or found the overflow, true iff the end state equals the start state.</summary>
public static class OverflowRestore
{
    public static OverflowPlan Decide(bool wasOpenAtStart, bool openedByTool, bool isOpenNow)
    {
        bool closeNow = openedByTool && isOpenNow;
        bool endOpen = isOpenNow && !closeNow;
        bool? restored = (openedByTool || wasOpenAtStart) ? endOpen == wasOpenAtStart : null;
        return new OverflowPlan(closeNow, restored);
    }
}
```

- [ ] **Step 4: Run** → `Passed: 5`.

- [ ] **Step 5: Commit** `git add src/FlaUI.Mcp.Core/Tray/OverflowRestore.cs test/FlaUI.Mcp.Tests/Tray/OverflowRestoreTests.cs && git commit -m "feat(tray): OverflowRestore decision"`.

- [ ] **Step 6: Headless gate** `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"` → all green (the redaction sweep must stay green: none of Part C reads `.Name`).

---

## Part D — Win32/UIA readers, WindowManager changes, the orchestrator

### Task 13: `TrayWin32` P/Invoke and `ClassicMenuReader` (K3)

SPEC "K3" row and §2 step 7 K3. `Win32Interop` (`src/FlaUI.Mcp.Core/Interaction/Win32Interop.cs`) already has `GetForegroundWindow`, `GetAncestor`, `WindowFromPhysicalPoint`, `GetWindowThreadProcessId`, `GetClassName`; the tray needs the rest in its own file.

**Files:** Create `src/FlaUI.Mcp.Core/Tray/TrayWin32.cs`, `src/FlaUI.Mcp.Core/Tray/ClassicMenuReader.cs`, `test/FlaUI.Mcp.Tests/Tray/ClassicMenuReaderTests.cs`.

- [ ] **Step 1: `TrayWin32.cs`**

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>Tray-specific user32 imports. Pure Win32, thread-agnostic, never blocks on another process
/// (GetWindowText/GetClassName/GetMenu* read cached state; SendMessage(MN_GETHMENU) is answered by the
/// menu window, which is modal-loop driven and responsive while the menu is up).</summary>
internal static class TrayWin32
{
    public const uint MN_GETHMENU = 0x01E1;
    public const uint MF_BYPOSITION = 0x400, MF_GRAYED = 0x1, MF_DISABLED = 0x2, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint SendMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(nint hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuStringW(nint hMenu, uint item, StringBuilder s, int max, uint flags);
    [DllImport("user32.dll")] public static extern uint GetMenuState(nint hMenu, uint item, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMenuItemRect(nint hwnd, nint hMenu, uint item, out RECT rect);

    public static string ClassOf(nint hwnd) { var sb = new StringBuilder(256); return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty; }
    public static bool HasTitle(nint hwnd) => GetWindowTextLengthW(hwnd) > 0;
    public static int PidOf(nint hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return (int)pid; }

    /// <summary>Every VISIBLE top-level HWND, titled or not (SPEC §2 step 2: no title filter).</summary>
    public static List<nint> VisibleTopLevel()
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) list.Add(h); return true; }, 0);
        return list;
    }

    /// <summary>First visible top-level window of the given class, or 0.</summary>
    public static nint FindVisibleByClass(string className)
        => VisibleTopLevel().FirstOrDefault(h => ClassOf(h) == className);
}
```

- [ ] **Step 2: Failing test for the pure part of the reader** (`ClassicMenuReaderTests.cs`):

```csharp
using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class ClassicMenuReaderTests
{
    [Fact]
    public void FromRaw_keeps_raw_positions_and_drops_separators_from_the_returned_list()
    {
        var raw = new[]
        {
            new RawMenuEntry(0, "&Alpha", TrayWin32.MF_BYPOSITION),
            new RawMenuEntry(1, "Disabled Item", TrayWin32.MF_GRAYED),
            new RawMenuEntry(2, "More", TrayWin32.MF_POPUP),
            new RawMenuEntry(3, "", TrayWin32.MF_SEPARATOR),
            new RawMenuEntry(4, "Beta\tCtrl+B", 0),
        };
        var items = ClassicMenuReader.FromRaw(raw);
        Assert.Equal(new[] { "Alpha", "Disabled Item", "More", "Beta" }, items.Select(i => i.Text));
        Assert.Equal(new[] { 0, 1, 2, 4 }, items.Select(i => i.RawPosition));
        Assert.Equal(new[] { true, false, true, true }, items.Select(i => i.Enabled));
        Assert.Equal(new[] { false, false, true, false }, items.Select(i => i.HasSubmenu));
    }

    [Fact]
    public void FromRaw_treats_MF_DISABLED_as_disabled_too()
        => Assert.False(ClassicMenuReader.FromRaw(new[] { new RawMenuEntry(0, "x", TrayWin32.MF_DISABLED) }).Single().Enabled);
}
```

- [ ] **Step 3: Run** `--filter "FullyQualifiedName~ClassicMenuReaderTests"` → build error.

- [ ] **Step 4: `ClassicMenuReader.cs`**

```csharp
using System.Text;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>One HMENU entry as Win32 reports it, before cleaning.</summary>
public readonly record struct RawMenuEntry(int Position, string RawText, uint State);

/// <summary>A readable K3 item. RawPosition indexes the HMENU INCLUDING separators, because
/// GetMenuItemRect/GetMenuState index by raw position (SPEC §2 step 7).</summary>
public sealed record ClassicMenuItem(int RawPosition, string Text, bool Enabled, bool HasSubmenu);

/// <summary>Reads a `#32768` menu window through Win32 (UIA sees nothing, M13).</summary>
public static class ClassicMenuReader
{
    /// <summary>The HMENU behind a `#32768` window, or 0 when it has none (then it is not K3).</summary>
    public static nint HmenuOf(nint menuHwnd) => TrayWin32.SendMessageW(menuHwnd, TrayWin32.MN_GETHMENU, 0, 0);

    public static List<RawMenuEntry> ReadRaw(nint hmenu)
    {
        var list = new List<RawMenuEntry>();
        int n = TrayWin32.GetMenuItemCount(hmenu);
        for (int i = 0; i < n; i++)
        {
            var sb = new StringBuilder(256);
            TrayWin32.GetMenuStringW(hmenu, (uint)i, sb, sb.Capacity, TrayWin32.MF_BYPOSITION);
            uint state = TrayWin32.GetMenuState(hmenu, (uint)i, TrayWin32.MF_BYPOSITION);
            list.Add(new RawMenuEntry(i, sb.ToString(), state));
        }
        return list;
    }

    /// <summary>Pure: clean labels, drop separators, keep raw positions.</summary>
    public static List<ClassicMenuItem> FromRaw(IEnumerable<RawMenuEntry> raw)
    {
        var items = new List<ClassicMenuItem>();
        foreach (var e in raw)
        {
            if ((e.State & TrayWin32.MF_SEPARATOR) != 0) continue;
            bool enabled = (e.State & (TrayWin32.MF_GRAYED | TrayWin32.MF_DISABLED)) == 0;
            bool submenu = (e.State & TrayWin32.MF_POPUP) != 0;
            items.Add(new ClassicMenuItem(e.Position, MenuLabel.Clean(e.RawText), enabled, submenu));
        }
        return items;
    }

    public static List<ClassicMenuItem> Read(nint hmenu) => FromRaw(ReadRaw(hmenu));

    /// <summary>Screen rect of a raw position, via GetMenuItemRect with a NULL window handle — MEASURED to
    /// work for a tray `#32768` menu (M17; Q2 covers 125 %). Null when Win32 refuses.</summary>
    public static (int X, int Y)? CentreOf(nint hmenu, int rawPosition)
    {
        if (!TrayWin32.GetMenuItemRect(0, hmenu, (uint)rawPosition, out var r)) return null;
        if (r.Right <= r.Left || r.Bottom <= r.Top) return null;
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }
}
```

(`GetMenuState` with `MF_BYPOSITION` returns `0xFFFFFFFF` for an invalid position; `GetMenuItemCount` bounds the loop, so that value never enters `FromRaw`. If Q2 answered "not physical at 125 %", multiply the rect by `GetDpiForWindow(menuHwnd)/96.0` here — add the import and a `menuHwnd` parameter — and say so in the commit.)

- [ ] **Step 5: Run** → `Passed: 2`. Headless gate green (no `.Name` reads here).

- [ ] **Step 6: Commit** `git add src/FlaUI.Mcp.Core/Tray/TrayWin32.cs src/FlaUI.Mcp.Core/Tray/ClassicMenuReader.cs test/FlaUI.Mcp.Tests/Tray/ClassicMenuReaderTests.cs && git commit -m "feat(tray): TrayWin32 imports and ClassicMenuReader (K3 via MN_GETHMENU)"`.

### Task 14: `WindowManager` — internal handle mint, STA hop by HWND, tray-window refusal

SPEC §1 ("`LazyHandleFor` … becomes `internal`") and §1.1 (close/transform refused for `Shell_TrayWnd` and the overflow, keyed on the HWND's class).

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs:302` (`private string LazyHandleFor` → `internal`), add three members, guard `CloseAsync` (671) and `WindowTransformAsync` (697).
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` (append two headless tests).

- [ ] **Step 1: Failing headless tests** (append inside the existing `WindowManagerTests` class; it already tests `internal static` pure members, e.g. `DeadWindowIds`):

```csharp
    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("TopLevelWindowForOverflowXamlIsland", true)]
    [InlineData("Shell_SecondaryTrayWnd", false)]
    [InlineData("Notepad", false)]
    [InlineData("", false)]
    public void IsTrayWindowClass_names_the_taskbar_and_the_overflow_only(string cls, bool expected)
        => Assert.Equal(expected, WindowManager.IsTrayWindowClass(cls));

    [Fact]
    public void RefuseIfTrayWindow_throws_ElementNotActionable_for_the_taskbar()
    {
        var ex = Assert.Throws<ToolException>(() => WindowManager.RefuseIfTrayWindow("Shell_TrayWnd", "closed"));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
        Assert.Contains("taskbar", ex.Message);
    }
```

(Add `using FlaUI.Mcp.Core.Errors;` at the top of the test file if it is not there.)

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~WindowManagerTests"` → build error.

- [ ] **Step 3: Edit `WindowManager.cs`.** Change line 302 to `internal string LazyHandleFor(IntPtr hwnd, int pid)`. Insert after the `LazyHandleFor` method (after its closing brace, before `private void TryWatchProcessExit`):

```csharp
    /// <summary>B2: mint (or reuse) a wN for an arbitrary HWND on the query STA — the tray tools' path to a
    /// handle for `Shell_TrayWnd` / the overflow window, which EnumTopLevel never lists (untitled).</summary>
    internal Task<string> MintHandleAsync(IntPtr hwnd, int pid)
        => _dispatcher.RunQueryAsync(() => LazyHandleFor(hwnd, pid));

    /// <summary>B2: run a read on the query STA against an element bound from a raw HWND (no handle needed).
    /// Same STA, same automation as every other read; the caller must not retain the element.</summary>
    internal Task<T> RunOnHwndAsync<T>(IntPtr hwnd, Func<AutomationElement, T> func)
        => _dispatcher.RunQueryAsync(() => func(_automation.FromHandle(hwnd)));

    /// <summary>SPEC B2 §1.1: the taskbar and its overflow are never closed, minimized or resized through a
    /// handle. Sending `Shell_TrayWnd` a close request is the classic way to raise "Shut Down Windows". Keyed
    /// on the window CLASS so a tray handle obtained any way is covered. Pure, headless-testable.</summary>
    internal static bool IsTrayWindowClass(string? className)
        => className is "Shell_TrayWnd" or "TopLevelWindowForOverflowXamlIsland";

    internal static void RefuseIfTrayWindow(string? className, string verb)
    {
        if (IsTrayWindowClass(className))
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                $"the taskbar cannot be {verb}: this handle is bound to the system tray.",
                "use desktop_tray_action for tray icons; the taskbar is not a window you close");
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }
```

In `CloseAsync` (line 671), replace the body's first statement so the refusal runs BEFORE `RunOnWindowAsync` and is NOT swallowed by the `catch (ToolException)`:

```csharp
    public async Task CloseAsync(WindowHandle handle)
    {
        if (_hwnds.TryGetValue(handle.Id, out var guardHwnd) && guardHwnd != IntPtr.Zero)
            RefuseIfTrayWindow(ClassNameOf(guardHwnd), "closed, minimized or resized");
        try
        {
            await RunOnWindowAsync(handle, w =>
```

(the rest of the method is unchanged). In `WindowTransformAsync` (line 697), add the same guard as the first line of the lambda:

```csharp
        RunOnWindowActionAsync(handle, (winEl, _) =>
        {
            RefuseIfTrayWindow(ClassNameOf(winEl.Properties.NativeWindowHandle.ValueOrDefault), "minimized, maximized or resized");
            var w = winEl.AsWindow();
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~WindowManagerTests"` → the two new tests pass; build clean.

- [ ] **Step 5: Commit** `git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs && git commit -m "feat(windows): internal handle mint by HWND; refuse close/transform on tray windows"`.

### Task 15: `TrayIconReader` (UIA icons, redacted names) and `TrayLocator` (taskbar, overflow, chevron)

SPEC "What Windows 11 actually exposes", "Finding the chevron", §1.2 locations, §3.4 redaction.

**Files:** Create `src/FlaUI.Mcp.Core/Tray/TrayIconReader.cs`, `src/FlaUI.Mcp.Core/Tray/TrayLocator.cs`. No headless test (both are UIA/Win32 bound); Task 19 pins them on the desktop.

- [ ] **Step 1: `TrayIconReader.cs`** — the ONLY place tooltips are read, and only through `ElementContent.Name`:

```csharp
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>One tray icon as the agent sees it. Tooltip is ALREADY redacted (SPEC §3.4) — Redacted says so.
/// Location: "visible" (NotifyItemIcon under Shell_TrayWnd), "system" (SystemTrayIcon under Shell_TrayWnd,
/// chevron excluded) or "hidden" (NotifyItemIcon in the overflow). RuntimeId re-finds the element on another
/// STA; the centre is in physical pixels (UIA BoundingRectangle).</summary>
public sealed record TrayIconInfo(string Tooltip, bool Redacted, string Location, string RuntimeId,
    int CentreX, int CentreY, bool IsOffscreen, nint HostHwnd);

/// <summary>UIA reads of the tray. Runs on whichever STA the caller is on. Names go through the same
/// egress accessor a snapshot uses, evaluated against the icons' REAL owner, explorer (SPEC §3.4).</summary>
public static class TrayIconReader
{
    public const string ExplorerProcess = "explorer";
    public const string NotifyItemAid = "NotifyItemIcon";
    public const string SystemItemAid = "SystemTrayIcon";
    public const string ChevronClass = "SystemTray.NormalButton";

    /// <summary>SPEC "Finding the chevron": the FIRST element in tree order with aid=SystemTrayIcon and
    /// class SystemTray.NormalButton. Null when there is no candidate (nothing hidden).</summary>
    public static string? FindChevronRuntimeId(AutomationElement tray)
    {
        foreach (var el in tray.FindAllDescendants(cf => cf.ByAutomationId(SystemItemAid)))
        {
            string? cls = null;
            try { cls = el.Properties.ClassName.ValueOrDefault; } catch { }
            if (cls == ChevronClass) return RidOf(el);
        }
        return null;
    }

    public static List<TrayIconInfo> ReadTaskbar(AutomationElement tray, nint trayHwnd, SensitivityClassifier classifier, string? chevronRid)
    {
        var list = new List<TrayIconInfo>();
        foreach (var el in tray.FindAllDescendants(cf => cf.ByAutomationId(NotifyItemAid)))
            list.Add(Describe(el, "visible", trayHwnd, classifier));
        foreach (var el in tray.FindAllDescendants(cf => cf.ByAutomationId(SystemItemAid)))
        {
            var info = Describe(el, "system", trayHwnd, classifier);
            if (chevronRid is not null && info.RuntimeId == chevronRid) continue; // the chevron is not an icon
            list.Add(info);
        }
        return list;
    }

    public static List<TrayIconInfo> ReadOverflow(AutomationElement overflow, nint overflowHwnd, SensitivityClassifier classifier)
    {
        var list = new List<TrayIconInfo>();
        foreach (var el in overflow.FindAllDescendants(cf => cf.ByAutomationId(NotifyItemAid)))
            list.Add(Describe(el, "hidden", overflowHwnd, classifier));
        return list;
    }

    public static AutomationElement? FindByRuntimeId(AutomationElement root, string rid)
    {
        foreach (var el in root.FindAllDescendants(cf => cf.ByAutomationId(NotifyItemAid).Or(cf.ByAutomationId(SystemItemAid))))
            if (RidOf(el) == rid) return el;
        return null;
    }

    internal static string RidOf(AutomationElement el)
    {
        try
        {
            var raw = el.Properties.RuntimeId.ValueOrDefault;
            return raw is null ? string.Empty : string.Join(".", raw.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        }
        catch { return string.Empty; }
    }

    private static TrayIconInfo Describe(AutomationElement el, string location, nint host, SensitivityClassifier classifier)
    {
        var read = ElementContent.Name(el, classifier, ExplorerProcess); // the egress accessor: [REDACTED] when a rule says so
        int cx = 0, cy = 0; bool off = true;
        try { var r = el.BoundingRectangle; cx = r.Left + r.Width / 2; cy = r.Top + r.Height / 2; off = r.Width <= 0 || r.Height <= 0; } catch { }
        try { off = off || el.Properties.IsOffscreen.ValueOrDefault; } catch { }
        return new TrayIconInfo(read.Text, read.Sensitivity.Redact, location, RidOf(el), cx, cy, off, host);
    }
}
```

- [ ] **Step 2: `TrayLocator.cs`**

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.UIA3;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>Finds the taskbar, the overflow window and the chevron; opens/closes the overflow and CONFIRMS
/// the effect by window presence, never by the chevron's (localized, state-bearing) Name (SPEC "Finding
/// the chevron", M14).</summary>
public sealed class TrayLocator
{
    private const int OverflowSettleMs = 1500;
    private readonly WindowManager _windows;
    private readonly AutomationDispatcher _dispatcher;

    public TrayLocator(WindowManager windows, AutomationDispatcher dispatcher) { _windows = windows; _dispatcher = dispatcher; }

    public static (nint Hwnd, int Pid) RequireTaskbar()
    {
        var hwnd = TrayWin32.FindWindowW(TrayMenuClassifier.TaskbarClass, null);
        if (hwnd == 0)
            throw new ToolException(ToolErrorCode.WindowNotFound, "taskbar not available; explorer may be restarting.", "retry in a few seconds");
        return (hwnd, TrayWin32.PidOf(hwnd));
    }

    public static nint OverflowHwnd() => TrayWin32.FindVisibleByClass(TrayMenuClassifier.OverflowClass);
    public static bool IsOverflowOpen() => OverflowHwnd() != 0;

    public Task<string?> ChevronRuntimeIdAsync(nint tray)
        => _windows.RunOnHwndAsync(tray, TrayIconReader.FindChevronRuntimeId);

    /// <summary>Returns the overflow HWND. Opens it if closed; 0 when there is no chevron (nothing hidden).
    /// Throws ElementNotActionable when invoking the chevron did not make the overflow appear.</summary>
    public async Task<nint> OpenOverflowAsync(nint tray)
    {
        var open = OverflowHwnd();
        if (open != 0) return open;
        var rid = await ChevronRuntimeIdAsync(tray);
        if (rid is null) return 0;
        await InvokeChevronAsync(tray, rid);
        var deadline = DateTime.UtcNow.AddMilliseconds(OverflowSettleMs);
        while (DateTime.UtcNow < deadline)
        {
            open = OverflowHwnd();
            if (open != 0) return open;
            await Task.Delay(50);
        }
        throw new ToolException(ToolErrorCode.ElementNotActionable,
            "could not open the hidden-icons overflow: invoking the chevron did not show it within 1500 ms.",
            "retry; if it persists the taskbar layout may have changed");
    }

    /// <summary>Best effort: invoke the chevron once if the overflow is open, wait for it to go. Never throws
    /// (cleanup never replaces a primary result, F3). Returns true when the overflow is closed afterwards.</summary>
    public async Task<bool> CloseOverflowAsync(nint tray)
    {
        try
        {
            if (!IsOverflowOpen()) return true;
            var rid = await ChevronRuntimeIdAsync(tray);
            if (rid is null) return !IsOverflowOpen();
            await InvokeChevronAsync(tray, rid);
            var deadline = DateTime.UtcNow.AddMilliseconds(OverflowSettleMs);
            while (DateTime.UtcNow < deadline && IsOverflowOpen()) await Task.Delay(50);
            return !IsOverflowOpen();
        }
        catch (ToolException) { return !IsOverflowOpen(); }
    }

    private Task InvokeChevronAsync(nint tray, string rid)
        => _dispatcher.RunActionAsync(() =>
        {
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(tray);
            var el = TrayIconReader.FindByRuntimeId(root, rid)
                     ?? throw new ToolException(ToolErrorCode.ElementNotActionable, "the hidden-icons chevron disappeared.", "retry");
            Interactor.Invoke(el);
        }, 4000);
}
```

- [ ] **Step 3: Build** `dotnet build FlaUI.Mcp.slnx -c Debug` → clean. Run the headless gate → green (the sweep accepts `ElementContent.Name(...)` invocations; `RidOf`, `ClassName`, `IsOffscreen`, `RuntimeId` are structural).

- [ ] **Step 4: Commit** `git add src/FlaUI.Mcp.Core/Tray/TrayIconReader.cs src/FlaUI.Mcp.Core/Tray/TrayLocator.cs && git commit -m "feat(tray): TrayIconReader (redacted tooltips) and TrayLocator (chevron rule, overflow open/close)"`.

### Task 16: `TrayManager` — lock, open, list, left-click, icon resolution

SPEC §1 (lock), §1.1, §1.2, §1.3 `left_click` + "Overflow after an action". Right-click is Task 17 (same file).

**Files:** Create `src/FlaUI.Mcp.Core/Tray/TrayManager.cs`, `test/FlaUI.Mcp.Tests/Tray/TrayManagerResolveTests.cs`.

- [ ] **Step 1: Failing headless tests for the pure resolution rules** (`TrayManagerResolveTests.cs`):

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayManagerResolveTests
{
    private static TrayIconInfo Icon(string tooltip, bool redacted = false, string loc = "visible")
        => new(tooltip, redacted, loc, "1.2.3", 10, 10, false, 0x1);

    [Fact]
    public void Blank_icon_query_is_InvalidArguments()
    {
        var ex = Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("ShareX") }, "  "));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void No_match_is_SelectorNoMatch()
        => Assert.Equal(ToolErrorCode.SelectorNoMatch, Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("ShareX") }, "vpn")).Code);

    [Fact]
    public void Two_matches_is_AmbiguousMatch_and_the_message_lists_both_names()
    {
        var ex = Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("Docker Desktop"), Icon("Docker helper") }, "docker"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Contains("Docker Desktop", ex.Message);
        Assert.Contains("Docker helper", ex.Message);
    }

    [Fact]
    public void A_redacted_icon_is_never_matched()
        => Assert.Equal(ToolErrorCode.SelectorNoMatch, Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("Bitwarden", redacted: true) }, "bitwarden")).Code);

    [Fact]
    public void Unique_match_returns_the_icon()
        => Assert.Equal("hidden", TrayManager.ResolveIcon(new[] { Icon("ShareX"), Icon("ZoomIt", loc: "hidden") }, "zoom").Location);
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TrayManagerResolveTests"` → build error.

- [ ] **Step 3: `TrayManager.cs`** (first half; Task 17 adds `RightClickAsync` to the same class):

```csharp
using System.Text.Json.Serialization;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.UIA3;

namespace FlaUI.Mcp.Core.Tray;

// ---- wire shapes (SPEC §1.1, §1.2, §1.3 "Result shapes"): absent fields are omitted, never null ----
public sealed record TrayOpenResult(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("overflowHandle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OverflowHandle,
    [property: JsonPropertyName("overflowLeftOpen")] bool OverflowLeftOpen);

public sealed record TrayIconDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("location")] string Location);

public sealed record TrayListResult(
    [property: JsonPropertyName("icons")] IReadOnlyList<TrayIconDto> Icons,
    [property: JsonPropertyName("overflowRestored"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OverflowRestored);

public sealed record TrayMenuItemDto(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("hasSubmenu")] bool HasSubmenu);

public sealed record TrayActionResult(
    [property: JsonPropertyName("matchedIcon")] string MatchedIcon,
    [property: JsonPropertyName("menuKind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MenuKind = null,
    [property: JsonPropertyName("items"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TrayMenuItemDto>? Items = null,
    [property: JsonPropertyName("selected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Selected = null,
    [property: JsonPropertyName("dismissed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Dismissed = null,
    [property: JsonPropertyName("handle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Handle = null,
    [property: JsonPropertyName("warning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Warning = null,
    [property: JsonPropertyName("overflowRestored"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OverflowRestored = null);

/// <summary>The tray as a driveable surface (ROADMAP B2). One process-wide lock serializes the three tools
/// (SPEC §1 F5) so two calls never toggle the overflow or take window baselines over each other.</summary>
public sealed class TrayManager
{
    public const int ReadLockTimeoutMs = 4000;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly WindowManager _windows;
    private readonly AutomationDispatcher _dispatcher;
    private readonly InputGuard _guard;
    private readonly IPlatformEnvironment _env;
    private readonly SensitivityClassifier _classifier;
    private readonly TrayLocator _locator;

    public TrayManager(WindowManager windows, AutomationDispatcher dispatcher, InputGuard guard,
        IPlatformEnvironment env, SensitivityClassifier classifier)
    {
        _windows = windows; _dispatcher = dispatcher; _guard = guard; _env = env; _classifier = classifier;
        _locator = new TrayLocator(windows, dispatcher);
    }

    internal async Task<T> WithLockAsync<T>(int timeoutMs, Func<Task<T>> body)
    {
        if (!await _lock.WaitAsync(timeoutMs))
            throw new ToolException(ToolErrorCode.Timeout, "another tray operation is in progress.", "retry when it has returned");
        try { return await body(); }
        finally { _lock.Release(); }
    }

    // ---- §1.1 ----
    public Task<TrayOpenResult> OpenTrayAsync(bool includeHidden) => WithLockAsync(ReadLockTimeoutMs, async () =>
    {
        var (tray, pid) = TrayLocator.RequireTaskbar();
        var handle = await _windows.MintHandleAsync(tray, pid);
        if (!includeHidden) return new TrayOpenResult(handle, null, false);
        var overflow = await _locator.OpenOverflowAsync(tray);
        if (overflow == 0) return new TrayOpenResult(handle, null, false); // nothing hidden: no overflow exists
        var overflowHandle = await _windows.MintHandleAsync(overflow, TrayWin32.PidOf(overflow));
        return new TrayOpenResult(handle, overflowHandle, true); // deliberately left open (§1.1)
    });

    // ---- §1.2 ----
    public Task<TrayListResult> ListIconsAsync(bool includeHidden) => WithLockAsync(ReadLockTimeoutMs, async () =>
    {
        var read = await ReadAllAsync(includeHidden);
        bool? restored = includeHidden ? await FinishOverflowAsync(read.TrayHwnd, read.WasOpen, read.OpenedByTool) : null;
        return new TrayListResult(read.Icons.Select(i => new TrayIconDto(i.Tooltip, i.Location)).ToList(), restored);
    });

    internal readonly record struct TrayRead(List<TrayIconInfo> Icons, nint TrayHwnd, int TrayPid, nint OverflowHwnd, bool WasOpen, bool OpenedByTool);

    /// <summary>Visible + system icons from the taskbar; hidden ones from the overflow when asked (opening it
    /// if needed). Order: visible, system, hidden, each in UIA tree order (§1.2).</summary>
    internal async Task<TrayRead> ReadAllAsync(bool includeHidden)
    {
        var (tray, pid) = TrayLocator.RequireTaskbar();
        bool wasOpen = TrayLocator.IsOverflowOpen();
        var chevronRid = await _locator.ChevronRuntimeIdAsync(tray);
        var icons = await _windows.RunOnHwndAsync(tray, el => TrayIconReader.ReadTaskbar(el, tray, _classifier, chevronRid));
        nint overflow = 0; bool opened = false;
        if (includeHidden)
        {
            overflow = await _locator.OpenOverflowAsync(tray);
            opened = overflow != 0 && !wasOpen;
            if (overflow != 0)
                icons.AddRange(await _windows.RunOnHwndAsync(overflow, el => TrayIconReader.ReadOverflow(el, overflow, _classifier)));
        }
        return new TrayRead(icons, tray, pid, overflow, wasOpen, opened);
    }

    /// <summary>§1.2 / §1.3 "Overflow after an action": apply OverflowRestore.Decide and report.</summary>
    internal async Task<bool?> FinishOverflowAsync(nint tray, bool wasOpen, bool openedByTool)
    {
        var plan = OverflowRestore.Decide(wasOpen, openedByTool, TrayLocator.IsOverflowOpen());
        if (plan.CloseNow) await _locator.CloseOverflowAsync(tray);
        return OverflowRestore.Decide(wasOpen, openedByTool, TrayLocator.IsOverflowOpen()).Restored;
    }

    // ---- §1.3 icon resolution (pure) ----
    internal static TrayIconInfo ResolveIcon(IReadOnlyList<TrayIconInfo> icons, string icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            throw new ToolException(ToolErrorCode.InvalidArguments, "icon must not be empty.", "pass part of the icon's tooltip, e.g. \"Docker\"");
        var matchable = icons.Where(i => !i.Redacted).ToList(); // a redacted tooltip cannot be matched (DEF-3 parity)
        var r = TrayMatch.IconBySubstring(matchable.Select(i => i.Tooltip).ToList(), icon.Trim());
        switch (r.Kind)
        {
            case MatchKind.Unique: return matchable[r.Index];
            case MatchKind.Ambiguous:
                var names = string.Join(" | ", r.Candidates.Select(c => matchable[c].Tooltip.Replace('\n', ' ')));
                throw new ToolException(ToolErrorCode.AmbiguousMatch, $"{r.Candidates.Count} tray icons match '{icon}': {names}", "pass a longer substring");
            default:
                throw new ToolException(ToolErrorCode.SelectorNoMatch, $"no tray icon matches '{icon}'.", "call desktop_list_tray_icons includeHidden:true and pick a name");
        }
    }

    // ---- §1.3 left_click ----
    public Task<TrayActionResult> LeftClickAsync(string icon, int timeoutMs) => WithLockAsync(timeoutMs, async () =>
    {
        var read = await ReadAllAsync(includeHidden: true);
        TrayIconInfo target;
        bool? restored;
        try
        {
            target = ResolveIcon(read.Icons, icon);
            await _dispatcher.RunActionAsync(() =>
            {
                using var automation = new UIA3Automation();
                var el = TrayIconReader.FindByRuntimeId(automation.FromHandle(target.HostHwnd), target.RuntimeId)
                         ?? throw new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "the icon disappeared before it could be invoked.", "re-list and retry");
                Interactor.Invoke(el);
            }, timeoutMs);
        }
        finally { restored = await FinishOverflowAsync(read.TrayHwnd, read.WasOpen, read.OpenedByTool); }
        return new TrayActionResult(target.Tooltip, OverflowRestored: restored);
    });
}
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~TrayManagerResolveTests"` → `Passed: 5`. Headless gate green (RULE 1: `TrayIconDto.Name` is a DECLARATION, not a member access; grep the new file for `.Name` to be sure there is none, e.g. `grep -n "\.Name" src/FlaUI.Mcp.Core/Tray/TrayManager.cs` → no output).

- [ ] **Step 5: Commit** `git add src/FlaUI.Mcp.Core/Tray/TrayManager.cs test/FlaUI.Mcp.Tests/Tray/TrayManagerResolveTests.cs && git commit -m "feat(tray): TrayManager open/list/left_click with the process-wide tray lock"`.

### Task 17: `UiaMenuReader` (K1) and `TrayManager.RightClickAsync` (§2 steps 1–8)

SPEC §2 entire, §3.1 (lease), §3.3 (deny-list before reading), §3.5 (hit-test re-verify), §4. Honour the Q-answers recorded in SPEC §8 (Q1: K1 selection by UIA Invoke or by click; Q2: rect scaling; Q6: candidate rule; Q9: K3 expected root).

**Files:** Create `src/FlaUI.Mcp.Core/Tray/UiaMenuReader.cs`; modify `src/FlaUI.Mcp.Core/Tray/TrayManager.cs` (add the members below inside the class).

- [ ] **Step 1: `UiaMenuReader.cs`** (runs on an action STA with its own automation; reads names only via `ElementContent.Name`):

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>A K1 item: cleaned text plus the facts §2 step 7 needs; RuntimeId re-finds it for Invoke.</summary>
public sealed record UiaMenuItem(string Text, bool Enabled, bool HasSubmenu, string RuntimeId);

/// <summary>The two UIA facts the classifier needs about a candidate window, plus the K1 item list.</summary>
public static class UiaMenuReader
{
    public static bool RootIsMenu(AutomationElement root)
    {
        try { return root.ControlType == ControlType.Menu; } catch { return false; }
    }

    private static AutomationElement[] MenuItems(AutomationElement root)
    {
        try { return root.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)); } catch { return Array.Empty<AutomationElement>(); }
    }

    /// <summary>True when at least one MenuItem supports Invoke or ExpandCollapse (M6 ShareX: both; M7
    /// OneDrive: neither — Value only — so a titled OneDrive window is also excluded by the title rule).</summary>
    public static bool HasActionableMenuItems(AutomationElement root)
        => MenuItems(root).Any(mi => Safe(() => mi.Patterns.Invoke.IsSupported) || Safe(() => mi.Patterns.ExpandCollapse.IsSupported));

    public static List<UiaMenuItem> Read(AutomationElement root, SensitivityClassifier classifier, string? processName)
    {
        var list = new List<UiaMenuItem>();
        foreach (var mi in MenuItems(root))
        {
            bool invoke = Safe(() => mi.Patterns.Invoke.IsSupported);
            bool expand = Safe(() => mi.Patterns.ExpandCollapse.IsSupported);
            if (!invoke && !expand) continue; // a separator or a non-actionable row
            var read = ElementContent.Name(mi, classifier, processName);
            bool enabled = Safe(() => mi.Properties.IsEnabled.ValueOrDefault);
            bool submenu = expand && (Safe(() => mi.FindAllChildren().Length > 0)
                                      || Safe(() => mi.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value != ExpandCollapseState.LeafNode));
            list.Add(new UiaMenuItem(MenuLabel.Clean(read.Text), enabled, submenu, TrayIconReader.RidOf(mi)));
        }
        return list;
    }

    public static AutomationElement? FindItem(AutomationElement root, string rid)
        => MenuItems(root).FirstOrDefault(mi => TrayIconReader.RidOf(mi) == rid);

    private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }
}
```

- [ ] **Step 2: Add to `TrayManager`** (inside the class, after `LeftClickAsync`):

```csharp
    // ---- §2 right_click ----
    private const int PollMs = 100, GoneSettleMs = 500, GonePollMs = 50;

    public Task<TrayActionResult> RightClickAsync(string icon, string? menuItem, int timeoutMs) => WithLockAsync(timeoutMs, async () =>
    {
        timeoutMs = AutomationDispatcher.ClampActionTimeout(timeoutMs);
        if (menuItem is not null && string.IsNullOrWhiteSpace(menuItem))
            throw new ToolException(ToolErrorCode.InvalidArguments, "menuItem must not be empty.", "omit it to list the menu, or pass part of an item's text");
        var started = DateTime.UtcNow;
        int Remaining() => Math.Max(250, timeoutMs - (int)(DateTime.UtcNow - started).TotalMilliseconds);

        var read = await ReadAllAsync(includeHidden: true);                       // step 1
        var baseline = TrayWin32.VisibleTopLevel().ToHashSet();                   // step 2
        TrayIconInfo target = null!;
        TrayActionResult? result = null; ToolException? failure = null;
        var tracked = new HashSet<nint>();                                        // every window this call treated as a candidate
        try
        {
            target = ResolveIcon(read.Icons, icon);   // inside the try: a no-match/ambiguous throw must still run step 8 (restore the overflow ReadAllAsync opened)
            // step 3: the click goes through the input pipeline (lease, deny-list, session, budget, audit, re-verify)
            if (target.IsOffscreen) throw new ToolException(ToolErrorCode.ElementNotActionable, "the icon is not on screen (hidden or auto-hidden taskbar).", "show the taskbar, then retry");
            var pt = _env.HitTestRoot(target.CentreX, target.CentreY);
            if (pt.Root != read.TrayHwnd && pt.Root != read.OverflowHwnd)
                throw new ToolException(ToolErrorCode.ElementNotActionable, "the icon's position is covered by another window.", "move that window, then retry");
            await Task.Run(() => _guard.MouseClick(target.CentreX, target.CentreY, "right", 1, Array.Empty<string>(),
                new ActionTarget(pt.Root, TrayWin32.PidOf(pt.Root), pt.ProcessName, pt.WindowClass)));

            // steps 4–5: poll until the picture is stable
            IReadOnlyList<ClassifiedWindow>? previous = null; IReadOnlyList<ClassifiedWindow> current = Array.Empty<ClassifiedWindow>();
            var deadline = started.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollMs);
                current = await ClassifyNewWindowsAsync(baseline, tracked, Remaining());
                if (TrayMenuClassifier.IsStable(previous, current)) break;
                previous = current;
            }
            var winner = TrayMenuClassifier.IsStable(previous, current) ? TrayMenuClassifier.Strongest(current) : null;
            if (winner is null)
            {
                var stillThere = (await ReadAllAsync(includeHidden: true)).Icons.Any(i => i.RuntimeId == target.RuntimeId);
                throw stillThere
                    ? new ToolException(ToolErrorCode.Timeout, "no menu appeared.", "raise timeoutMs, or the app has no right-click menu")
                    : new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "the icon disappeared when clicked; its app is probably no longer running.", "re-list the tray");
            }
            var w = winner.Value;

            // step 6: deny-list before reading anything
            int pid = TrayWin32.PidOf(w.Hwnd);
            var proc = ProcessIdentity.OfPid(pid);
            if (PerceptionPolicy.IsDenied(proc))
                throw new ToolException(ToolErrorCode.TargetDenied, $"the menu belongs to a denied process ('{proc ?? "unknown"}').", "this app's menu is never read");

            // step 7
            result = w.Kind switch
            {
                MenuKind.AppWindow => await HandleWindowAsync(target, w.Hwnd, pid, menuItem),
                MenuKind.ClassicMenu => await HandleClassicAsync(target, w.Hwnd, menuItem),
                _ => await HandleUiaAsync(target, w.Hwnd, proc, menuItem, Remaining()),
            };
        }
        catch (ToolException ex) { failure = ex; }
        finally
        {
            // step 8: cleanup never replaces the primary outcome (F3)
            var (dismissed, warning) = await DismissLeftoversAsync(baseline, tracked, read.TrayPid);
            bool? restored = await FinishOverflowAsync(read.TrayHwnd, read.WasOpen, read.OpenedByTool);
            if (failure is not null)
            {
                failure = warning is null ? failure : new ToolException(failure.Code, failure.Message + " " + warning, failure.SuggestedRecovery);
            }
            else if (result is not null)
            {
                if (result.MenuKind == "menu") result = result with { Dismissed = result.Dismissed ?? dismissed };
                if (warning is not null) result = result with { Warning = result.Warning is null ? warning : result.Warning + " " + warning };
                result = result with { OverflowRestored = restored };
            }
        }
        if (failure is not null) throw failure;
        return result!;
    });

    /// <summary>Step 4+5: classify every visible top-level window not in the baseline, ignoring the classes
    /// §2 step 4 names. UIA facts are read on the action STA under the remaining budget (F9); a candidate
    /// whose read parks is treated as unreadable this poll.</summary>
    private async Task<IReadOnlyList<ClassifiedWindow>> ClassifyNewWindowsAsync(HashSet<nint> baseline, HashSet<nint> tracked, int budgetMs)
    {
        var fg = TrayWin32.GetForegroundWindow();
        int fgPid = fg == 0 ? -1 : TrayWin32.PidOf(fg);
        var list = new List<ClassifiedWindow>();
        foreach (var h in TrayWin32.VisibleTopLevel())
        {
            if (baseline.Contains(h)) continue;
            var cls = TrayWin32.ClassOf(h);
            if (TrayMenuClassifier.IsIgnoredClass(cls)) continue;
            int pid = TrayWin32.PidOf(h);
            if (pid != fgPid) continue;
            bool hasTitle = TrayWin32.HasTitle(h);
            bool hasHmenu = cls == TrayMenuClassifier.ClassicMenuClass && ClassicMenuReader.HmenuOf(h) != 0;
            bool rootIsMenu = false, actionable = false; int count = 0;
            if (!hasHmenu)
            {
                try
                {
                    (rootIsMenu, actionable, count) = await _dispatcher.RunActionAsync(() =>
                    {
                        using var automation = new UIA3Automation();
                        var root = automation.FromHandle(h);
                        var items = UiaMenuReader.Read(root, _classifier, ProcessIdentity.OfPid(pid));
                        return (UiaMenuReader.RootIsMenu(root), UiaMenuReader.HasActionableMenuItems(root), items.Count);
                    }, budgetMs);
                }
                catch (ToolException) { continue; } // parked or stale this poll
            }
            else count = ClassicMenuReader.Read(ClassicMenuReader.HmenuOf(h)).Count;
            var kind = TrayMenuClassifier.Classify(new CandidateFacts(h, cls, hasTitle, pid, h == fg, hasHmenu, rootIsMenu, actionable), fgPid);
            if (kind == MenuKind.None) continue;
            tracked.Add(h);
            list.Add(new ClassifiedWindow(h, kind, count));
        }
        return list;
    }

    private static TrayMenuItemDto[] ToDto(IEnumerable<(string Text, bool Enabled, bool HasSubmenu)> items)
        => items.Select(i => new TrayMenuItemDto(i.Text, i.Enabled, i.HasSubmenu)).ToArray();

    /// <summary>§2 step 7 menuItem match + the two refusals, shared by K1 and K3. Returns the chosen index.</summary>
    private static int ChooseItem(IReadOnlyList<(string Text, bool Enabled, bool HasSubmenu)> items, string menuItem)
    {
        var r = TrayMatch.ItemByText(items.Select(i => i.Text).ToList(), menuItem.Trim());
        var listing = string.Join(" | ", items.Select(i => i.Text));
        if (r.Kind == MatchKind.None)
            throw new ToolException(ToolErrorCode.SelectorNoMatch, $"no menu item matches '{menuItem}'. Items: {listing}", "pass one of the listed items");
        if (r.Kind == MatchKind.Ambiguous)
            throw new ToolException(ToolErrorCode.AmbiguousMatch, $"{r.Candidates.Count} menu items match '{menuItem}': {string.Join(" | ", r.Candidates.Select(c => items[c].Text))}", "pass the exact item text");
        var it = items[r.Index];
        if (!it.Enabled) throw new ToolException(ToolErrorCode.ElementNotActionable, $"menu item '{it.Text}' is disabled.", "pick an enabled item");
        if (it.HasSubmenu) throw new ToolException(ToolErrorCode.ElementNotActionable, $"menu item '{it.Text}' opens a submenu; submenus are not supported.", "pick a leaf item");
        return r.Index;
    }

    private async Task<TrayActionResult> HandleWindowAsync(TrayIconInfo target, nint hwnd, int pid, string? menuItem)
    {
        var handle = await _windows.MintHandleAsync(hwnd, pid);
        string? warning = menuItem is null ? null
            : "the icon opened an application window, not a menu; nothing was selected. Use desktop_snapshot / desktop_click on the handle.";
        return new TrayActionResult(target.Tooltip, MenuKind: "window", Handle: handle, Warning: warning);
    }

    private async Task<TrayActionResult> HandleClassicAsync(TrayIconInfo target, nint menuHwnd, string? menuItem)
    {
        var hmenu = ClassicMenuReader.HmenuOf(menuHwnd);
        var items = ClassicMenuReader.Read(hmenu);
        var dto = ToDto(items.Select(i => (i.Text, i.Enabled, i.HasSubmenu)));
        if (menuItem is null) return new TrayActionResult(target.Tooltip, MenuKind: "menu", Items: dto);
        int idx = ChooseItem(items.Select(i => (i.Text, i.Enabled, i.HasSubmenu)).ToList(), menuItem);
        if (!TrayWin32.IsWindow(menuHwnd) || !TrayWin32.IsWindowVisible(menuHwnd))
            throw new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "the menu closed before the item could be selected.", "retry");
        var centre = ClassicMenuReader.CentreOf(hmenu, items[idx].RawPosition)
                     ?? throw new ToolException(ToolErrorCode.ElementNotActionable, "the menu item has no screen rectangle.", "retry");
        var pt = _env.HitTestRoot(centre.X, centre.Y);
        if (pt.Root != menuHwnd)
            throw new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "the point no longer belongs to the menu.", "retry");
        await Task.Run(() => _guard.MouseClick(centre.X, centre.Y, "left", 1, Array.Empty<string>(),
            new ActionTarget(menuHwnd, TrayWin32.PidOf(menuHwnd), pt.ProcessName, pt.WindowClass)));
        bool gone = await WaitGoneAsync(menuHwnd);
        return new TrayActionResult(target.Tooltip, MenuKind: "menu", Items: dto, Selected: items[idx].Text,
            Dismissed: gone, Warning: gone ? null : "the menu is still visible after the selection.");
    }

    private async Task<TrayActionResult> HandleUiaAsync(TrayIconInfo target, nint menuHwnd, string? proc, string? menuItem, int budgetMs)
    {
        var items = await _dispatcher.RunActionAsync(() =>
        {
            using var automation = new UIA3Automation();
            return UiaMenuReader.Read(automation.FromHandle(menuHwnd), _classifier, proc);
        }, budgetMs);
        var dto = ToDto(items.Select(i => (i.Text, i.Enabled, i.HasSubmenu)));
        if (menuItem is null) return new TrayActionResult(target.Tooltip, MenuKind: "menu", Items: dto);
        int idx = ChooseItem(items.Select(i => (i.Text, i.Enabled, i.HasSubmenu)).ToList(), menuItem);
        if (!TrayWin32.IsWindow(menuHwnd) || !TrayWin32.IsWindowVisible(menuHwnd))
            throw new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "the menu closed before the item could be selected.", "retry");
        // Q1 decided the K1 selection path. If Q1 measured YES, this is it:
        await _dispatcher.RunActionAsync(() =>
        {
            using var automation = new UIA3Automation();
            var el = UiaMenuReader.FindItem(automation.FromHandle(menuHwnd), items[idx].RuntimeId)
                     ?? throw new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "the menu item disappeared before it could be invoked.", "retry");
            Interactor.Invoke(el);
        }, budgetMs);
        // If Q1 measured NO: replace the block above with a left-click at the item's BoundingRectangle centre through
        // _guard.MouseClick with expectedRoot = _env.HitTestRoot(centre).Root (which must equal menuHwnd), as HandleClassicAsync does.
        bool gone = await WaitGoneAsync(menuHwnd);
        return new TrayActionResult(target.Tooltip, MenuKind: "menu", Items: dto, Selected: items[idx].Text,
            Dismissed: gone, Warning: gone ? null : "the menu is still visible after the selection.");
    }

    /// <summary>A selected menu fades out while still visible (panel r2): poll up to 500 ms.</summary>
    private static async Task<bool> WaitGoneAsync(nint hwnd)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(GoneSettleMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!TrayWin32.IsWindow(hwnd) || !TrayWin32.IsWindowVisible(hwnd)) return true;
            await Task.Delay(GonePollMs);
        }
        return !TrayWin32.IsWindow(hwnd) || !TrayWin32.IsWindowVisible(hwnd);
    }

    /// <summary>§2 step 8: one last scan for K1/K3 windows of the foreground process (a late menu, F2) plus
    /// anything this call tracked; if any is still up, Escape through the input pipeline targeting the CURRENT
    /// foreground root, then verify. Never throws. Returns (dismissed, warning).</summary>
    private async Task<(bool Dismissed, string? Warning)> DismissLeftoversAsync(HashSet<nint> baseline, HashSet<nint> tracked, int explorerPid)
    {
        var fg = TrayWin32.GetForegroundWindow();
        int fgPid = fg == 0 ? -1 : TrayWin32.PidOf(fg);
        var up = TrayWin32.VisibleTopLevel()
            .Where(h => !baseline.Contains(h) && !TrayMenuClassifier.IsIgnoredClass(TrayWin32.ClassOf(h)))
            .Where(h => tracked.Contains(h) || (TrayWin32.PidOf(h) == fgPid && !TrayWin32.HasTitle(h) && fgPid != explorerPid))
            .Where(h => TrayWin32.IsWindowVisible(h))
            .ToList();
        if (up.Count == 0) return (true, null);
        try
        {
            var root = _env.GetForegroundRoot();
            if (root == 0) return (false, "a menu is still open and there is no foreground window to send Escape to.");
            var r = _env.ResolveRoot(root);
            await Task.Run(() => _guard.KeyChord(Array.Empty<string>(), "Esc", new ActionTarget(root, TrayWin32.PidOf(root), r.ProcessName, r.WindowClass)));
        }
        catch (ToolException ex)
        {
            return (false, $"a menu may still be open: Escape was refused ({ex.Code}).");
        }
        var deadline = DateTime.UtcNow.AddMilliseconds(GoneSettleMs);
        while (DateTime.UtcNow < deadline && up.Any(h => TrayWin32.IsWindow(h) && TrayWin32.IsWindowVisible(h))) await Task.Delay(GonePollMs);
        bool gone = !up.Any(h => TrayWin32.IsWindow(h) && TrayWin32.IsWindowVisible(h));
        return (gone, gone ? null : "a menu window is still visible after Escape.");
    }
```

Notes for the implementer:
- `ProcessIdentity.OfPid` is `src/FlaUI.Mcp.Core/Perception/ProcessIdentity.cs:84`; `PerceptionPolicy.IsDenied` fails closed on null (`PerceptionPolicy.cs:54`).
- `InputGuard.MouseClick` / `KeyChord` (`InputGuard.cs:123-133`) run the lease, deny-list, session and budget gates and the leaf's hit-test / foreground re-verify (`Win32SyntheticInput.cs:43,55`). The `"Esc"` token is the one `KeyChordParser` knows (`KeyChordParser.cs:28`).
- Only a K1/K3 result carries `dismissed`; a K2 window is never dismissed (§2 step 7 K2), so the `finally` only fills `Dismissed` when `MenuKind == "menu"`.
- If Q6 recorded a kind whose window is not in the foreground process, extend `CandidateFacts` with `OwnerPid` (GW_OWNER chain via `GetWindow(h, 4)`) and let `Classify` accept `OwnerPid == foregroundPid` too; add a headless test for it in `TrayMenuClassifierTests`.

- [ ] **Step 3: Build** → clean; headless gate → green (check `grep -n "\.Name\b" src/FlaUI.Mcp.Core/Tray/*.cs` prints only the `ElementContent.Name(` invocations).

- [ ] **Step 4: Commit** `git add src/FlaUI.Mcp.Core/Tray && git commit -m "feat(tray): right_click sequence (baseline, poll, classify K1/K2/K3, select, cleanup)"`.

---

## Part E — tools, desktop tests, docs, mutation checks

### Task 18: `TrayTools` (the three MCP tools), DI, and the contract tests

SPEC §1.1–§1.3, §3.2 (read-only posture), §6 "Routing" (descriptions).

**Files:** Create `src/FlaUI.Mcp.Server/Tools/TrayTools.cs`, `test/FlaUI.Mcp.Tests/Tray/TrayToolsContractTests.cs`; modify `src/FlaUI.Mcp.Server/Program.cs` (after line 182 `builder.Services.AddSingleton<InputTools>();`).

- [ ] **Step 1: Failing contract tests** (headless; the invariant suites in `test/FlaUI.Mcp.Tests/Server/ToolReadOnlyInvariantTests.cs` and `ToolTrapFactInvariantTests.cs` discover the new tools by reflection and add their own checks automatically):

```csharp
using System.ComponentModel;
using System.Reflection;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayToolsContractTests
{
    private static MethodInfo M(string name) => typeof(TrayTools).GetMethod(name)!;
    private static string Desc(string name) => M(name).GetCustomAttribute<DescriptionAttribute>()!.Description;
    private static bool Explicit(string name, string arg)
        => M(name).GetCustomAttributesData().First(d => d.AttributeType == typeof(McpServerToolAttribute))
            .NamedArguments.Any(a => a.MemberName == arg && a.TypedValue.Value is true);

    [Theory]
    [InlineData("DesktopOpenTray")]
    [InlineData("DesktopListTrayIcons")]
    public void The_read_tools_are_ReadOnly(string name) => Assert.True(Explicit(name, "ReadOnly"));

    [Fact]
    public void Tray_action_is_Destructive() => Assert.True(Explicit("DesktopTrayAction", "Destructive"));

    [Fact]
    public void Tray_action_defaults_timeoutMs_to_4000()
        => Assert.Equal(4000, M("DesktopTrayAction").GetParameters().Single(p => p.Name == "timeoutMs").DefaultValue);

    [Fact]
    public void Open_tray_description_routes_right_click_menus_to_tray_action()
        => Assert.Contains("desktop_tray_action", Desc("DesktopOpenTray"));

    [Fact]
    public void Tray_action_description_tells_the_agent_to_check_matchedIcon()
        => Assert.Contains("matchedIcon", Desc("DesktopTrayAction"));

    [Fact]
    public void List_description_states_the_tooltip_redaction_limit()
        => Assert.Contains("explorer", Desc("DesktopListTrayIcons"));

    [Fact]
    public async Task Tray_action_with_an_unknown_action_is_InvalidArguments_before_any_dependency_is_touched()
    {
        var tools = new TrayTools(tray: null!, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var json = await tools.DesktopTrayAction("ShareX", "double_click");
        Assert.Contains("InvalidArguments", json);
    }

    [Fact]
    public async Task Tray_action_with_a_negative_timeout_is_InvalidArguments()
    {
        var tools = new TrayTools(tray: null!, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var json = await tools.DesktopTrayAction("ShareX", "left_click", timeoutMs: -1);
        Assert.Contains("InvalidArguments", json);
    }

    [Fact]
    public void Desktop_click_description_routes_tray_right_clicks_to_tray_action()
        => Assert.Contains("desktop_tray_action",
            typeof(InputTools).GetMethod(nameof(InputTools.DesktopClick))!.GetCustomAttribute<DescriptionAttribute>()!.Description);
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TrayToolsContractTests"` → build error.

- [ ] **Step 3: `TrayTools.cs`**

```csharp
using System.ComponentModel;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Tray;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>ROADMAP B2: the system tray as a driveable surface. Thin over TrayManager (Core).</summary>
[McpServerToolType]
public sealed class TrayTools
{
    private const int DefaultTimeoutMs = 4000;
    private readonly TrayManager _tray;
    private readonly ServerOptions _options;

    public TrayTools(TrayManager tray, ServerOptions options) { _tray = tray; _options = options; }

    [McpServerTool(ReadOnly = true), Description("Return a window handle (wN) for the Windows taskbar's system tray, usable with desktop_snapshot/desktop_find/desktop_invoke. includeHidden:true also opens the hidden-icons overflow and returns overflowHandle; the overflow is deliberately LEFT OPEN (overflowLeftOpen:true) and closes by itself when the foreground moves, after which overflowHandle fails WindowHandleStale: call this again. Tray handles cannot be closed, minimized or resized (ElementNotActionable). For an icon's RIGHT-CLICK menu do NOT desktop_click through this handle (the menu appears as an untitled window you cannot open): use desktop_tray_action. To list icons by name use desktop_list_tray_icons. Lease-exempt; available in --read-only-mode.")]
    public Task<string> DesktopOpenTray(
        [Description("Also open the hidden-icons overflow and return its handle (default false).")] bool includeHidden = false)
        => ToolResponse.Guard(async () => ToolResponse.Ok(await _tray.OpenTrayAsync(includeHidden)));

    [McpServerTool(ReadOnly = true), Description("List tray icons: { icons: [{ name, location }], overflowRestored? }. name is the icon's tooltip as UIA reports it (multi-line, localized, state-bearing; it is set by the app and is NOT an identity: any app can call its icon \"Docker\"). location = visible | system (Network, Volume, Clock...) | hidden (in the overflow; only with includeHidden:true, which opens the overflow and closes it again, reporting overflowRestored). Redaction rules apply to names, evaluated against the icons' real owner, explorer: a rule scoped to another process does not hide that app's tooltip; a global or explorer-scoped rule does. Lease-exempt; available in --read-only-mode.")]
    public Task<string> DesktopListTrayIcons(
        [Description("Open the overflow to include hidden icons, then restore it (default false).")] bool includeHidden = false)
        => ToolResponse.Guard(async () => ToolResponse.Ok(await _tray.ListIconsAsync(includeHidden)));

    [McpServerTool(Destructive = true), Description("Act on a tray icon by tooltip substring (case-insensitive; 0 matches SelectorNoMatch, >1 AmbiguousMatch listing the names; hidden icons are found too). ALWAYS check matchedIcon in the reply: a tooltip is app-chosen and any app can imitate another's. action=left_click: UIA Invoke on the icon (NO input lease required); returns { matchedIcon, overflowRestored? }. action=right_click: real right-click, REQUIRES an active input lease (`flaui-mcp unlock`); reads what appears and returns menuKind:\"menu\" with items [{ text, enabled, hasSubmenu }] (classic Win32 and UIA menus alike) or menuKind:\"window\" with a handle when the app opened its own window instead (OneDrive-style; nothing is selected then). Pass menuItem to select an item: exact text first, then substring; disabled items and submenus are refused (ElementNotActionable); selected + dismissed are returned. Without menuItem the menu is listed and dismissed with Escape. A menu of a credential-store process is refused (TargetDenied) before it is read. Blocked in --read-only-mode.")]
    public Task<string> DesktopTrayAction(
        [Description("Tooltip substring of the icon, e.g. \"Docker\".")] string icon,
        [Description("left_click | right_click")] string action,
        [Description("right_click only: text of the menu item to select (exact, then substring). Omit to list the menu.")] string? menuItem = null,
        [Description("Max ms to wait for the menu (default 4000, capped at 60000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            AutomationDispatcher.ClampActionTimeout(timeoutMs); // negative → InvalidArguments, before any dependency
            var a = action?.Trim().ToLowerInvariant();
            TrayActionResult r = a switch
            {
                "left_click" => await _tray.LeftClickAsync(icon, timeoutMs),
                "right_click" => await _tray.RightClickAsync(icon, menuItem, timeoutMs),
                _ => throw new ToolException(ToolErrorCode.InvalidArguments, $"unknown action '{action}'.", "use left_click or right_click"),
            };
            return ToolResponse.Ok(r);
        });
}
```

Check each description's length (`ToolTrapFactInvariantTests` ceiling 1500): the `DesktopTrayAction` text above is close to the ceiling; if the invariant fails, shorten the parenthetical examples, never raise the budget.

- [ ] **Step 4: Register in `Program.cs`** after `builder.Services.AddSingleton<InputTools>();` (line 182):

```csharp
// --- ROADMAP B2: system tray ---
builder.Services.AddSingleton(sp => new FlaUI.Mcp.Core.Tray.TrayManager(
    sp.GetRequiredService<WindowManager>(),
    sp.GetRequiredService<AutomationDispatcher>(),
    sp.GetRequiredService<InputGuard>(),
    sp.GetRequiredService<IPlatformEnvironment>(),
    sp.GetRequiredService<FlaUI.Mcp.Core.Perception.SensitivityClassifier>()));
builder.Services.AddSingleton<TrayTools>();
```

(`SensitivityClassifier` is already registered at line 102: `builder.Services.AddSingleton(classifier);`. `.WithToolsFromAssembly()` discovers `[McpServerToolType]` classes.)

- [ ] **Step 5: `desktop_click` description** (`InputTools.cs:413`): append to the existing text, before `Requires an active input lease`: `For a TRAY icon's right-click menu use desktop_tray_action instead: the menu appears as an untitled window this tool cannot open. ` Keep the whole description under 1500 chars.

- [ ] **Step 6: Run** `--filter "FullyQualifiedName~TrayToolsContractTests"` → `Passed: 11`; then the headless gate → green (the invariants: posture statements present, exactly one of ReadOnly/Destructive, descriptions under budget, read-only short-circuit).

- [ ] **Step 7: Wire check over stdio** (console or RDP, no lease needed): `dotnet build FlaUI.Mcp.slnx -c Debug`, then start `src/FlaUI.Mcp.Server/bin/Debug/net10.0-windows10.0.19041.0/win-x64/FlaUI.Mcp.Server.exe` (the exe `StdioProtocolCleanlinessTests.LocateServerExe` finds) and send `tools/list`; confirm `desktop_open_tray`, `desktop_list_tray_icons`, `desktop_tray_action` are present. The simplest route is the flaui-mcp plugin already installed in this session: after `just plugin` is NOT needed for this check — the installed plugin runs the shipped v1.0.1 exe, so instead reuse the `McpStdioServer` helper from `test/FlaUI.Mcp.Tests/Vision/DesktopFindTextScaledTests.cs:76` in a throwaway test, or pipe `{"jsonrpc":"2.0","id":1,"method":"initialize",...}` + `tools/list` as `StdioProtocolCleanlinessTests` does.

- [ ] **Step 8: Commit** `git add src/FlaUI.Mcp.Server/Tools/TrayTools.cs src/FlaUI.Mcp.Server/Program.cs src/FlaUI.Mcp.Server/Tools/InputTools.cs test/FlaUI.Mcp.Tests/Tray/TrayToolsContractTests.cs && git commit -m "feat(server): desktop_open_tray, desktop_list_tray_icons, desktop_tray_action"`.

### Task 19: Desktop tests — open, list, handles, left-click, refusals

SPEC §7 "Desktop" row. Needs the fixture (Task 5/6) and the Q5 answer (where the fixture icon lands; the tests below assume **hidden**, with the F10 guard that fails loudly otherwise).

**Files:** Modify `test/FlaUI.Mcp.Tests/Tray/TrayDesktopTests.cs` (replace the Task 6 skeleton with this class; keep the smoke test).

- [ ] **Step 1: Write the tests**

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Tray;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

[Trait("Category", "Desktop")]
public class TrayDesktopTests
{
    /// <summary>Everything a tray test needs, built the way Program.cs builds it (real Win32 env, real sink,
    /// file lease provider — the read tools and left_click never consult the lease).</summary>
    private sealed class Rig : IDisposable
    {
        public AutomationDispatcher Dispatcher { get; } = new();
        public WindowManager Windows { get; }
        public TrayManager Tray { get; }
        public TrayTools Tools { get; }
        public Rig()
        {
            Windows = new WindowManager(Dispatcher);
            var env = new Win32PlatformEnvironment();
            var guard = new InputGuard(new Win32SyntheticInput(env), env, new FileLeaseProvider(), new ActionBudget(),
                new InputAudit(TextWriter.Null), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, false, false);
            Tray = new TrayManager(Windows, Dispatcher, guard, env, SensitivityClassifier.OsOnly);
            Tools = new TrayTools(Tray, new ServerOptions(ReadOnly: false, AllowElevation: false));
        }
        public void Dispose() { Windows.Dispose(); Dispatcher.Dispose(); }
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    /// <summary>F10: a hidden-path test must PROVE it is on the hidden path. Fails naming Q5 otherwise.</summary>
    private static void RequireHidden(JsonElement list, string tooltip)
    {
        var icon = list.GetProperty("icons").EnumerateArray().Single(i => i.GetProperty("name").GetString() == tooltip);
        Assert.True(icon.GetProperty("location").GetString() == "hidden",
            $"the fixture icon is '{icon.GetProperty("location")}', not 'hidden' — see SPEC §8 Q5; this test would not exercise the hidden path");
    }

    [Fact]
    public void Fixture_starts_logs_and_exits_gracefully()
    {
        string log;
        using (var fx = new TrayFixtureHost("uia")) { log = fx.LogPath; Assert.Contains(fx.Events(), e => e.StartsWith("started kind=uia")); }
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task List_without_hidden_shows_system_icons_and_never_the_overflow()
    {
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopListTrayIcons());
        Assert.Contains(j.GetProperty("icons").EnumerateArray(), i => i.GetProperty("location").GetString() == "system");
        Assert.False(j.TryGetProperty("overflowRestored", out _));
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [Fact]
    public async Task List_with_hidden_contains_the_fixture_icon_with_its_exact_tooltip_and_restores_a_closed_overflow()
    {
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        Assert.False(TrayLocator.IsOverflowOpen());
        var j = Json(await rig.Tools.DesktopListTrayIcons(includeHidden: true));
        RequireHidden(j, fx.Tooltip);
        Assert.True(j.GetProperty("overflowRestored").GetBoolean());
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [Fact]
    public async Task List_with_hidden_leaves_an_already_open_overflow_open()
    {
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        var opened = Json(await rig.Tools.DesktopOpenTray(includeHidden: true));
        Assert.True(opened.GetProperty("overflowLeftOpen").GetBoolean());
        try
        {
            var j = Json(await rig.Tools.DesktopListTrayIcons(includeHidden: true));
            RequireHidden(j, fx.Tooltip);
            Assert.True(j.GetProperty("overflowRestored").GetBoolean());
            Assert.True(TrayLocator.IsOverflowOpen());
        }
        finally { await rig.Tray.ListIconsAsync(false); await new TrayLocator(rig.Windows, rig.Dispatcher).CloseOverflowAsync(TrayLocator.RequireTaskbar().Hwnd); }
    }

    [Fact]
    public async Task Open_tray_handles_work_with_find_and_the_overflow_handle_goes_stale_once_closed()
    {
        using var rig = new Rig();
        var perception = new PerceptionManager(rig.Windows, new RefRegistry(), new SnapshotCache());
        var j = Json(await rig.Tools.DesktopOpenTray(includeHidden: true));
        var tray = new WindowHandle(j.GetProperty("handle").GetString()!);
        var found = await perception.FindAsync(tray, new FindQuery(TrayIconReader.SystemItemAid, null, "eq", null, false), 50, null);
        Assert.NotEmpty(found.Matches);
        if (j.TryGetProperty("overflowHandle", out var oh))
        {
            var overflow = new WindowHandle(oh.GetString()!);
            Assert.NotEmpty((await perception.FindAsync(overflow, new FindQuery(TrayIconReader.NotifyItemAid, null, "eq", null, false), 50, null)).Matches);
            await new TrayLocator(rig.Windows, rig.Dispatcher).CloseOverflowAsync(TrayLocator.RequireTaskbar().Hwnd);
            rig.Windows.PruneClosedWindows();
            var ex = await Assert.ThrowsAsync<FlaUI.Mcp.Core.Errors.ToolException>(() => perception.FindAsync(overflow, new FindQuery(TrayIconReader.NotifyItemAid, null, "eq", null, false), 50, null));
            Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.WindowHandleStale, ex.Code);
        }
    }

    [Fact]
    public async Task Tray_handle_refuses_close_and_transform()
    {
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopOpenTray());
        var handle = j.GetProperty("handle").GetString()!;
        var windowTools = new WindowTools(rig.Windows, new ServerOptions(false, false), new Win32PlatformEnvironment());
        Assert.Contains("ElementNotActionable", await windowTools.DesktopCloseWindow(handle));
        var interaction = new InteractionTools(new PerceptionManager(rig.Windows, new RefRegistry(), new SnapshotCache()), rig.Windows, new ServerOptions(false, false));
        Assert.Contains("ElementNotActionable", await interaction.DesktopWindowTransform(handle, "minimize"));
        Assert.True(TrayWin32.IsWindow(TrayLocator.RequireTaskbar().Hwnd)); // still there
    }

    [Fact]
    public async Task Left_click_reaches_the_fixture_and_restores_the_overflow()
    {
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "left_click"));
        Assert.Equal(fx.Tooltip, j.GetProperty("matchedIcon").GetString());
        Assert.True(fx.WaitForEvent("left-click"), $"fixture log: {string.Join(" / ", fx.Events())}");
        Assert.True(j.GetProperty("overflowRestored").GetBoolean());
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [Fact]
    public async Task Ambiguous_icon_lists_both_fixture_names()
    {
        using var a = new TrayFixtureHost("uia", "FlaUI Tray Twin");
        using var b = new TrayFixtureHost("uia", "FlaUI Tray Twin");
        using var rig = new Rig();
        var json = await rig.Tools.DesktopTrayAction("FlaUI Tray Twin", "left_click");
        Assert.Contains("AmbiguousMatch", json);
        Assert.Contains(a.Tooltip, json);
        Assert.Contains(b.Tooltip, json);
    }

    [Fact]
    public async Task Unknown_icon_is_SelectorNoMatch()
    {
        using var rig = new Rig();
        Assert.Contains("SelectorNoMatch", await rig.Tools.DesktopTrayAction("no such tray icon 9f3a", "left_click"));
    }
}
```

`FindQuery(AutomationId, Name, NameMatch, ControlType, EnabledOnly, IgnoreCase=false)` is `src/FlaUI.Mcp.Core/Perception/FindQuery.cs:19`; `PerceptionManager.FindAsync(handle, query, max, scopeRef)` is `PerceptionManager.cs:557`; `FindResult.Matches` is `FindQuery.cs:42`.

- [ ] **Step 2: Run on the console** `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~TrayDesktopTests"` → all pass. If `List_with_hidden_...` fails at `RequireHidden`, Q5 was recorded as visible: flip the assertions to `visible` per the Q5 cell and rename the tests.

- [ ] **Step 3: Commit** `git add test/FlaUI.Mcp.Tests/Tray/TrayDesktopTests.cs && git commit -m "test(tray): Desktop tests for open/list/handles/left_click/refusals"`.

### Task 20: Desktop + SyntheticInput tests — right-click K1/K3/K2, selection, errors

SPEC §7 "Desktop + SyntheticInput" row. Lease required: tests skip without one (`Skip.If(InputLocked())`). Run on the session the operator names; the fixture's menus are the K1/K3/K2 targets, so no third-party app is touched. The visible-icon variant depends on Q7 (Task 8): implement it only if Q7 measured that promotion works; otherwise omit it and cite the Stand-downs line.

**Files:** Create `test/FlaUI.Mcp.Tests/Tray/TrayRightClickTests.cs`.

- [ ] **Step 1: Write the tests**

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Tray;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

[Trait("Category", "Desktop")]
[Trait("Category", "SyntheticInput")]
public class TrayRightClickTests
{
    private static bool InputLocked()
    {
        var lease = new FileLeaseProvider().Read(out _);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        return lease is null || !lease.IsValidNow(DateTime.UtcNow, sid);
    }
    private const string NoLease = "no active input lease — grant one on the console with `flaui-mcp unlock`";

    private sealed class Rig : IDisposable
    {
        public AutomationDispatcher Dispatcher { get; } = new();
        public WindowManager Windows { get; }
        public TrayTools Tools { get; }
        public Rig()
        {
            Windows = new WindowManager(Dispatcher);
            var env = new Win32PlatformEnvironment();
            var guard = new InputGuard(new Win32SyntheticInput(env), env, new FileLeaseProvider(), new ActionBudget(),
                new InputAudit(TextWriter.Null), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, false, false);
            Tools = new TrayTools(new TrayManager(Windows, Dispatcher, guard, env, SensitivityClassifier.OsOnly),
                new ServerOptions(ReadOnly: false, AllowElevation: false));
        }
        public void Dispose() { Windows.Dispose(); Dispatcher.Dispose(); }
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    private static string[] Texts(JsonElement j) => j.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()!).ToArray();
    private static readonly string[] ExpectedItems = { "Alpha", "Disabled Item", "More", "Beta", "Exit" }; // separator dropped, accelerators/shortcut cleaned

    [SkippableTheory]
    [InlineData("uia")]
    [InlineData("classic")]
    public async Task Right_click_lists_the_fixture_menu_and_dismisses_it(string kind)
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost(kind);
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click"));
        Assert.Equal(fx.Tooltip, j.GetProperty("matchedIcon").GetString());
        Assert.Equal("menu", j.GetProperty("menuKind").GetString());
        Assert.Equal(ExpectedItems, Texts(j));
        var items = j.GetProperty("items").EnumerateArray().ToArray();
        Assert.False(items[1].GetProperty("enabled").GetBoolean());
        Assert.True(items[2].GetProperty("hasSubmenu").GetBoolean());
        Assert.True(j.GetProperty("dismissed").GetBoolean());
        Assert.True(fx.WaitForEvent("menu closed"), string.Join(" / ", fx.Events()));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("command "));
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [SkippableTheory]
    [InlineData("uia")]
    [InlineData("classic")]
    public async Task Selecting_an_item_lands_in_the_fixture_log_and_the_menu_is_gone(string kind)
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost(kind);
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: "beta"));
        Assert.Equal("Beta", j.GetProperty("selected").GetString());
        Assert.True(j.GetProperty("dismissed").GetBoolean());
        Assert.True(fx.WaitForEvent("command Beta"), string.Join(" / ", fx.Events()));
        Assert.Equal(0, TrayWin32.FindVisibleByClass(TrayMenuClassifier.ClassicMenuClass));
    }

    [SkippableFact]
    public async Task Exact_item_text_wins_over_a_substring_hit()
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        // "Alpha" is exact; nothing else contains it, so this pins the equality-first rule end to end.
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: "ALPHA"));
        Assert.Equal("Alpha", j.GetProperty("selected").GetString());
        Assert.True(fx.WaitForEvent("command Alpha"));
    }

    [SkippableTheory]
    [InlineData("Disabled Item", "ElementNotActionable")]
    [InlineData("More", "ElementNotActionable")]
    [InlineData("Nope", "SelectorNoMatch")]
    public async Task Refused_items_dismiss_the_menu_and_send_nothing(string item, string code)
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost("classic");
        using var rig = new Rig();
        var json = await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: item);
        Assert.Contains(code, json);
        Assert.True(fx.WaitForEvent("menu closed"), string.Join(" / ", fx.Events()));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("command "));
        Assert.Equal(0, TrayWin32.FindVisibleByClass(TrayMenuClassifier.ClassicMenuClass));
    }

    [SkippableFact]
    public async Task K2_returns_a_window_handle_and_a_warning_when_menuItem_was_given()
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost("window");
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: "Alpha"));
        Assert.Equal("window", j.GetProperty("menuKind").GetString());
        var handle = j.GetProperty("handle").GetString()!;
        Assert.Contains("nothing was selected", j.GetProperty("warning").GetString());
        Assert.False(j.TryGetProperty("dismissed", out _));
        Assert.True(fx.WaitForEvent("window opened"));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("command "));
        // the handle is usable: close the fixture window through it
        var windowTools = new WindowTools(rig.Windows, new ServerOptions(false, false), new Win32PlatformEnvironment());
        Assert.DoesNotContain("\"error\"", await windowTools.DesktopCloseWindow(handle));
        Assert.True(fx.WaitForEvent("window closed"));
    }

    [SkippableFact]
    public async Task Right_click_without_a_lease_is_InputNotLeased_and_touches_nothing()
    {
        // Runs ONLY when the lease is absent — the inverse gate of every other test here.
        Skip.If(!InputLocked(), "an input lease is active; this test needs the locked state");
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        Assert.Contains("InputNotLeased", await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click"));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("menu "));
        Assert.False(TrayLocator.IsOverflowOpen());
    }
}
```

If Q7 measured that `IsPromoted=1` promotes the icon, add this test (arrange: find the fixture's `HKCU\Control Panel\NotifyIconSettings\*` key by `ExecutablePath`, set `IsPromoted=1`, restart the fixture if Q7 said "after restart", assert `location == "visible"` via `desktop_list_tray_icons`, then run the same right-click/list assertions as `Right_click_lists_the_fixture_menu_and_dismisses_it("uia")`; restore the value in `finally`). Name it `Right_click_on_a_VISIBLE_icon_lists_the_menu`.

- [ ] **Step 2: Run with a lease on the console** (`flaui-mcp unlock --minutes 30` first): `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~TrayRightClickTests"` → all pass, 1 skipped (the locked-state test). Then run once **without** a lease (`flaui-mcp lock` or wait for expiry) → the locked-state test passes, the others skip.

- [ ] **Step 3: Commit** `git add test/FlaUI.Mcp.Tests/Tray/TrayRightClickTests.cs && git commit -m "test(tray): right_click K1/K3/K2, selection, refusals (SyntheticInput)"`.

### Task 21: Docs — agent contract, driving skill, CHANGELOG, ROADMAP

SPEC §6.

**Files:** Modify `docs/agent-contract.md`, `.claude/skills/driving-flaui-mcp/SKILL.md` (then `just plugin`), `CHANGELOG.md`, `ROADMAP.md`.

- [ ] **Step 1: `docs/agent-contract.md`.** Insert a new subsection before `### Opaque-App Access` (line 156):

```markdown
### System Tray
The notification area as a driveable surface. Icons are identified by **tooltip substring only**: a tooltip is
app-chosen (any app can name its icon "Docker"), so always check `matchedIcon` in a reply.

| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_open_tray` | ReadOnly | Handle for the taskbar (`Shell_TrayWnd`), usable with snapshot/find/invoke. `includeHidden` also opens the hidden-icons overflow and returns `overflowHandle`, **left open** (`overflowLeftOpen`); it closes by itself when the foreground moves, after which the handle is `WindowHandleStale`. Tray handles refuse `desktop_close_window` / `desktop_window_transform` (`ElementNotActionable`). Lease-exempt. |
| `desktop_list_tray_icons` | ReadOnly | `{ icons: [{ name, location }], overflowRestored? }`, `location` = `visible` \| `system` \| `hidden`. `includeHidden` opens and restores the overflow. Lease-exempt. |
| `desktop_tray_action` | Destructive | `icon` (substring), `action` = `left_click` (UIA Invoke, lease-exempt) \| `right_click` (real input, **lease required**), `menuItem?`, `timeoutMs` (default 4000). Blocked in `--read-only-mode`. |

`right_click` returns one of three shapes:

| `menuKind` | When | Fields |
|---|---|---|
| `"menu"` | a UIA menu (WinForms/WPF) or a classic Win32 menu appeared | `matchedIcon`, `items: [{ text, enabled, hasSubmenu }]`, `selected?`, `dismissed`, `overflowRestored?`, `warning?` |
| `"window"` | the app opened its own titled window instead (OneDrive-style) | `matchedIcon`, `handle` (use snapshot/click on it), `warning?`, `overflowRestored?` |

`menuItem` matches exact text first, then substring. Disabled items and submenus are refused
(`ElementNotActionable`); the menu is dismissed with Escape on every error path. A menu owned by a credential-store
process is refused (`TargetDenied`) before any item is read.

**Tooltip redaction limit.** Icon names pass through the same redaction rules as a snapshot, evaluated against the
icons' real owner, **explorer**: a rule scoped to a password manager's process does **not** hide that manager's tray
tooltip; a global or explorer-scoped rule does. Redacted icons cannot be matched by `icon`.

**Out of scope (v1):** submenus, `double_click`, secondary-monitor taskbars, auto-hidden taskbars (the right-click is
refused `ElementNotActionable`), selecting inside a `"window"` result.
```

Add to the `### By error code` table (line 204+) one row:

```markdown
| `Timeout` (`desktop_tray_action`) | No menu settled within `timeoutMs` after the right-click, or another tray call holds the tray lock. | Retry once with a larger `timeoutMs`; if the icon's app has no right-click menu, stop. |
```

Add to `## Known Tool Limitations` (line 239):

```markdown
- Tray icons carry no owner process: `desktop_tray_action` matches tooltips, and a redaction rule scoped to an app's process does not reach that app's tray tooltip (explorer owns the icon).
```

- [ ] **Step 2: Driving skill.** In `.claude/skills/driving-flaui-mcp/SKILL.md`, under `## Synthetic input needs a human lease` (line 166), add one bullet at the end of that section:

```markdown
- **System tray:** read icons with `desktop_list_tray_icons includeHidden:true`; act with `desktop_tray_action`
  (`left_click` needs no lease; `right_click` lists the menu or selects `menuItem` under the lease). Never
  `desktop_click` a tray icon through the `desktop_open_tray` handle for its menu: the menu is an untitled window
  you cannot open. Always check `matchedIcon`.
```

Then `just plugin` (regenerates `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` from the seed; `PluginDistributionTests` pins the twin). Run `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~PluginDistributionTests|FullyQualifiedName~PluginArtifactWriterTests"` → green.

- [ ] **Step 3: `CHANGELOG.md`** under `## [Unreleased]`:

```markdown
### Added

- **System tray as a driveable surface (ROADMAP B2).** Three tools: `desktop_open_tray` (a handle for the taskbar,
  optionally the hidden-icons overflow), `desktop_list_tray_icons` (`name` + `location` visible/system/hidden) and
  `desktop_tray_action` (`left_click` via UIA Invoke, `right_click` under the input lease: lists the menu or selects
  `menuItem`, for WinForms/WPF menus, classic Win32 menus and OneDrive-style windows alike). Icons are matched by
  tooltip substring; every reply echoes `matchedIcon`. Tray handles refuse close/minimize/resize. Measured on
  Windows 11 (`NotifyIconOverflowWindow` no longer exists; the overflow is `TopLevelWindowForOverflowXamlIsland`).
```

- [ ] **Step 4: `ROADMAP.md:103-107`** replace the B2 section body with:

```markdown
### B2 — System tray as a driveable surface (`desktop_open_tray`) — ✅ shipped

Shipped <date> (`<sha of Task 18's commit>`): `desktop_open_tray`, `desktop_list_tray_icons`, `desktop_tray_action`.
Spec: `docs/superpowers/specs/2026-11-06-b2-system-tray-design.md`; plan:
`docs/superpowers/plans/2026-11-06-b2-system-tray.md`. The original text named `NotifyIconOverflowWindow`, which
no longer exists on Windows 11: hidden icons live in a transient `TopLevelWindowForOverflowXamlIsland` window that
exists only while the overflow is open. Out of scope for v1: submenus, double-click, secondary taskbars,
auto-hidden taskbars.
```

- [ ] **Step 5: Headless gate** → green. **Commit** `git add docs/agent-contract.md .claude/skills/driving-flaui-mcp/SKILL.md plugins/flaui-mcp CHANGELOG.md ROADMAP.md && git commit -m "docs(tray): agent contract, driving skill route, changelog, ROADMAP B2 shipped"`.

### Task 22: Mutation checks and the full gates

SPEC §7 "Mutation checks": each mutation must turn at least one test red. Apply each with a temporary edit, run the named filter, confirm red, `git checkout -- <file>`, confirm green. Record the four outcomes in the final commit message.

- [ ] **Mutation 1 — remove the K3 Win32 path:** in `TrayManager.ClassifyNewWindowsAsync` set `bool hasHmenu = false;`. Run `--filter "FullyQualifiedName~TrayRightClickTests"` (lease on) → `Right_click_lists_the_fixture_menu_and_dismisses_it(kind: "classic")` fails (Timeout / no menu). Revert.
- [ ] **Mutation 2 — skip the overflow restore:** in `OverflowRestore.Decide` return `new OverflowPlan(false, restored)`. Run `--filter "FullyQualifiedName~OverflowRestoreTests|FullyQualifiedName~TrayDesktopTests"` → `Tool_opened_it_and_it_is_still_open_closes_and_reports_restored` and `List_with_hidden_contains_the_fixture_icon...` fail. Revert.
- [ ] **Mutation 3 — accept the first of an ambiguous match:** in `TrayMatch.FromHits` change the `_ =>` arm to `new MatchResult(MatchKind.Unique, hits[0], hits)`. Run `--filter "FullyQualifiedName~TrayMatchTests|FullyQualifiedName~TrayManagerResolveTests"` → the ambiguity tests fail. Revert.
- [ ] **Mutation 4 — report `selected` without sending the selection:** in `HandleClassicAsync` delete the `_guard.MouseClick(...)` call (and in `HandleUiaAsync` the `Interactor.Invoke(el)` call). Run `--filter "FullyQualifiedName~TrayRightClickTests"` → `Selecting_an_item_lands_in_the_fixture_log...` fails on `WaitForEvent("command Beta")` for both kinds. Revert.
- [ ] **Full gates:** `dotnet build FlaUI.Mcp.slnx -c Debug` clean; headless gate green; `pwsh -File scripts/headless-gate.ps1` green (license headers on every new file); on the console with a lease: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~Tray"` all green; then the whole Desktop gate `pwsh -File scripts/desktop-suite.ps1` (it excludes SyntheticInput; the tray SyntheticInput tests skip without a lease and ran above) → report the pass count from `artifacts/desktop-runs/desktop-<stamp>.txt`.
- [ ] **Commit** anything the gates touched (`just plugin` drift, `artifacts/` is ignored) with `chore(tray): mutation checks 4/4 red, desktop gate <n>/<n>`.

### Task 23: Wrap-up — review gates, memory index, push

- [ ] **Step 1: AGY-CAPSTONE** (`clavity:agy-capstone`) over the committed code and tests, rounds until GREEN; fold verified findings as fix commits; then **AGY-TEST-AUDIT** (`clavity:agy-test-audit`). Record the ledger entries in `docs/agy-capstone-ledger.md` as that file's existing entries do.
- [ ] **Step 2: Memory index.** Update `C:\Users\user\.claude\projects\E--C--flauimcp\memory\project_flauimcp_execution.md`: B2 tasks with their SHAs, the Q1–Q9 answers in one line each, and the ▶ resume point (`B4` next, then PSSA; v1.1.0 cut via `scripts/release.ps1` once the operator says so).
- [ ] **Step 3: Push** `git push` after the operator confirms, then `/watch-ci`.
- [ ] **Step 4: Release.** `v1.1.0` is cut after B2 lands (SPEC header). Run `pwsh -File scripts/release.ps1 -WhatIf` and show the operator the preview; before the real cut, run the `ScaledDisplay` check on RDP as `docs/building.md` "Cutting a release" requires (ask before switching sessions). The cut itself is the operator's call.

---

## Self-review (run by the author, 2026-11-06)

**Spec coverage.** §1 lock → Task 16 `WithLockAsync`; §1.1 → Tasks 14, 16, 18, 19; §1.2 → Tasks 12, 15, 16, 19; §1.3 left_click, matching, `matchedIcon`, `InvalidArguments` on blank/unknown action → Tasks 10, 16, 18, 19; §2 steps 1–8 → Task 17 (classifier Task 11, K3 reader Task 13, K1 reader Task 17, cleanup `DismissLeftoversAsync`); §3.1 lease → Task 17 via `InputGuard`; §3.2 read-only → Task 18 (`Guard` vs `GuardWrite`); §3.3 deny-list → Task 17 step 6; §3.4 redaction → Tasks 15, 16 (`ResolveIcon` skips redacted), 21; §3.5 re-verify → Task 17 (pre-click hit-test + leaf re-verify); §4 error table → Tasks 16–18; §5 out of scope → Task 21 docs, Q3 rule in Task 17; §6 docs/routing → Tasks 18, 21; §7 fixture/tests/mutations → Tasks 5, 6, 19, 20, 22; §8 Q1–Q9 → Tasks 1–4, 7, 8, each before the code that depends on it (Q1 → Task 17 K1 path; Q2 → Task 13 rect; Q3 → Task 17 pre-click; Q4/Q5 → Tasks 19/20 assertions; Q6 → Task 11 rule; Q7 → Task 20 visible variant; Q8 → Task 15 chevron rule; Q9 → Task 17 K3 expected root).

**Placeholders.** None: every code step carries its code; the two conditional branches (Q1 = no, Q6 = owner chain) state the exact replacement.

**Type consistency.** `TrayIconInfo(Tooltip, Redacted, Location, RuntimeId, CentreX, CentreY, IsOffscreen, HostHwnd)` is used identically in Tasks 15, 16, 17 and the resolve tests; `ClassifiedWindow(Hwnd, Kind, ItemCount)` in Tasks 11 and 17; `TrayActionResult` named arguments match its record in Task 16; `TrayLocator.RequireTaskbar()` is static and returns `(Hwnd, Pid)` everywhere; `WindowManager.MintHandleAsync` / `RunOnHwndAsync` / `RefuseIfTrayWindow` / `IsTrayWindowClass` as declared in Task 14; `TrayFixtureHost.Events()/WaitForEvent()` as declared in Task 6.

**Known deliberate gap.** The plan does not add a headless test for `TrayLocator`/`TrayIconReader`/`UiaMenuReader` (UIA-bound); they are pinned by the Desktop tests and the capstone must weigh whether a fake-automation seam is worth adding.
