// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Text.Json.Serialization;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Core.AutomationElements;
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
            try
            {
                overflow = await _locator.OpenOverflowAsync(tray);
                opened = overflow != 0 && !wasOpen;
                if (overflow != 0)
                    icons.AddRange(await _windows.RunOnHwndAsync(overflow, el => TrayIconReader.ReadOverflow(el, overflow, _classifier)));
            }
            catch
            {
                if (!wasOpen && TrayLocator.IsOverflowOpen())
                {
                    try { await _locator.CloseOverflowAsync(tray); } catch { }
                }
                throw;
            }
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
    public Task<TrayActionResult> LeftClickAsync(string icon, int timeoutMs)
    {
        timeoutMs = AutomationDispatcher.ClampActionTimeout(timeoutMs);
        return WithLockAsync(timeoutMs, async () =>
    {
        var read = await ReadAllAsync(includeHidden: true);
        TrayIconInfo target;
        bool? restored = null;
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
        finally
        {
            try { restored = await FinishOverflowAsync(read.TrayHwnd, read.WasOpen, read.OpenedByTool); }
            catch (Exception ex) when (ex is not OperationCanceledException) { restored = null; }
        }
        return new TrayActionResult(target.Tooltip, OverflowRestored: restored);
        });
    }

    // ---- §2 right_click ----
    private const int PollMs = 100, GoneSettleMs = 500, GonePollMs = 50, IconSettleMs = 1500, IconSettlePollMs = 60;

    private static (int X, int Y, bool Off)? ReadCentre(AutomationElement? el)
    {
        if (el is null) return null;
        int cx = 0, cy = 0; bool off = true;
        try { var r = el.BoundingRectangle; cx = r.Left + r.Width / 2; cy = r.Top + r.Height / 2; off = r.Width <= 0 || r.Height <= 0; } catch { }
        try { off = off || el.Properties.IsOffscreen.ValueOrDefault; } catch { }
        return (cx, cy, off);
    }

    /// <summary>Overflow icons slide for ~500 ms after the chevron opens. Re-read the target's centre until two
    /// consecutive reads agree, then check on the same STA that UIA hit-testing that point yields the target itself
    /// (or a near ancestor). Anything else refuses: a stale point lands on a neighbouring app's icon.</summary>
    private async Task<TrayIconInfo> RefreshTargetAsync(TrayIconInfo target)
    {
        var gone = new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "the icon disappeared before it could be clicked.", "re-list and retry");
        var settleStart = DateTime.UtcNow;
        (int X, int Y, bool Off)? prev = null;
        while (true)
        {
            var cur = await _windows.RunOnHwndAsync(target.HostHwnd, root => ReadCentre(TrayIconReader.FindByRuntimeId(root, target.RuntimeId)));
            if (cur is null) throw gone;
            if (prev is { } p && p.X == cur.Value.X && p.Y == cur.Value.Y)
            {
                var (cx, cy, off) = cur.Value;
                if (!off)
                {
                    var ok = await _windows.RunOnHwndAsync(target.HostHwnd, root =>
                    {
                        try
                        {
                            var el = TrayIconReader.FindByRuntimeId(root, target.RuntimeId);
                            if (el is null) return (bool?)null;
                            var again = ReadCentre(el);
                            if (again is null || again.Value.X != cx || again.Value.Y != cy) return false;
                            var hit = el.Automation.FromPoint(new System.Drawing.Point(cx, cy));
                            for (int i = 0; hit is not null && i <= 3; i++, hit = hit.Parent)
                                if (TrayIconReader.RidOf(hit) == target.RuntimeId) return true;
                            return false;
                        }
                        catch (Exception) { return false; }
                    });
                    if (ok is null) throw gone;
                    if (ok == false) throw new ToolException(ToolErrorCode.ElementNotActionable, "the icon's position is covered or moved; refusing to click.", "retry");
                }
                return target with { CentreX = cx, CentreY = cy, IsOffscreen = off };
            }
            prev = cur;
            if ((DateTime.UtcNow - settleStart).TotalMilliseconds >= IconSettleMs)
                throw new ToolException(ToolErrorCode.ElementNotActionable, "the icon is still moving; retry.", "retry in a moment");
            await Task.Delay(IconSettlePollMs);
        }
    }

    public Task<TrayActionResult> RightClickAsync(string icon, string? menuItem, int timeoutMs)
    {
        timeoutMs = AutomationDispatcher.ClampActionTimeout(timeoutMs);
        return WithLockAsync(timeoutMs, async () =>
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
        bool clicked = false;
        try
        {
            target = ResolveIcon(read.Icons, icon);   // inside the try: a no-match/ambiguous throw must still run step 8 (restore the overflow ReadAllAsync opened)
            target = await RefreshTargetAsync(target);   // overflow icons animate: re-read until still, then prove the point is the icon (fail closed)
            // step 3: the click goes through the input pipeline (lease, deny-list, session, budget, audit, re-verify)
            if (target.IsOffscreen) throw new ToolException(ToolErrorCode.ElementNotActionable, "the icon is not on screen (hidden or auto-hidden taskbar).", "show the taskbar, then retry");
            var pt = _env.HitTestRoot(target.CentreX, target.CentreY);
            if (pt.Root != read.TrayHwnd && pt.Root != read.OverflowHwnd)
                throw new ToolException(ToolErrorCode.ElementNotActionable, "the icon's position is covered by another window.", "move that window, then retry");
            await Task.Run(() => _guard.MouseClick(target.CentreX, target.CentreY, "right", 1, Array.Empty<string>(),
                new ActionTarget(pt.Root, TrayWin32.PidOf(pt.Root), pt.ProcessName, pt.WindowClass)));
            clicked = true;

            // steps 4–5: poll until the picture is stable
            IReadOnlyList<ClassifiedWindow>? previous = null; IReadOnlyList<ClassifiedWindow> current = Array.Empty<ClassifiedWindow>();
            var deadline = started.AddMilliseconds(timeoutMs);
            bool stable = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollMs);
                current = await ClassifyNewWindowsAsync(baseline, tracked, Remaining());
                if (TrayMenuClassifier.IsStable(previous, current)) { stable = true; break; }
                previous = current;
            }
            var winner = stable ? TrayMenuClassifier.Strongest(current) : null;
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
            bool dismissed = false; string? warning = null; bool? restored = null;
            if (clicked)
            {
                try { (dismissed, warning) = await DismissLeftoversAsync(baseline, tracked, read.TrayPid); }
                catch (Exception ex) when (ex is not OperationCanceledException) { (dismissed, warning) = (false, "cleanup failed: " + ex.GetType().FullName); }
            }
            try { restored = await FinishOverflowAsync(read.TrayHwnd, read.WasOpen, read.OpenedByTool); }
            catch (Exception ex) when (ex is not OperationCanceledException) { restored = null; }
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
    }

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
            var denied = PerceptionPolicy.IsDenied(ProcessIdentity.OfPid(pid));
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
                        int n = denied ? 0 : UiaMenuReader.Read(root, _classifier, ProcessIdentity.OfPid(pid)).Count; // SPEC §2 step 6: never read a denied process's menu
                        return (UiaMenuReader.RootIsMenu(root), UiaMenuReader.HasActionableMenuItems(root), n);
                    }, budgetMs);
                }
                catch (ToolException) { continue; } // parked or stale this poll
            }
            else count = denied ? 0 : ClassicMenuReader.Read(ClassicMenuReader.HmenuOf(h)).Count;
            var kind = TrayMenuClassifier.Classify(new CandidateFacts(h, cls, hasTitle, pid, h == fg, hasHmenu, rootIsMenu, actionable), fgPid);
            if (kind == MenuKind.None) continue;
            if (kind != MenuKind.AppWindow) tracked.Add(h); // K2 (an app window) is never Escaped
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
}
