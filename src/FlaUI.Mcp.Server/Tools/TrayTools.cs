// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

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
