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
