using System.ComponentModel;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class FindTools
{
    private readonly PerceptionManager _perception;
    public FindTools(PerceptionManager perception) => _perception = perception;

    [McpServerTool(ReadOnly = true), Description(
        "Query a window for element refs WITHOUT walking the whole tree - the cheap way to target one " +
        "control. Give any of: automationId, name (+ nameMatch=eq|contains), controlType (UIA name e.g. " +
        "Button/Edit/ListItem), enabledOnly. Optional scope=<a live ref> searches only that element's " +
        "subtree. Returns matches[{ref,automationId,name,controlType,bounds[x,y,w,h],isOffscreen,isEnabled," +
        "hasFocus}] in tree order (capped at max, default 20) plus totalMatches + isTruncated (narrow your " +
        "query if truncated). No match => empty list (not an error). Refs are additive: a find does NOT " +
        "invalidate a prior desktop_snapshot's refs. Password fields return name=\"[REDACTED]\" and are " +
        "not findable by name.")]
    public Task<string> DesktopFind(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Exact UIA AutomationId to match.")] string? automationId = null,
        [Description("Name to match (see nameMatch).")] string? name = null,
        [Description("eq (exact) or contains (substring). Default eq.")] string nameMatch = "eq",
        [Description("UIA ControlType name, e.g. Button, Edit, ListItem.")] string? controlType = null,
        [Description("Only return enabled elements (default false).")] bool enabledOnly = false,
        [Description("Max matches returned (default 20).")] int max = 20,
        [Description("Optional live ref to search within (its subtree only).")] string? scope = null,
        [Description("Case-insensitive name match (eq and contains). Default false (exact/ordinal). The Phase-10 selector defaults this true; set it here to preview selector matching.")] bool ignoreCase = false)
        => ToolResponse.Guard(async () =>
        {
            var query = new FindQuery(automationId, name, nameMatch, controlType, enabledOnly, ignoreCase);
            var r = await _perception.FindAsync(new WindowHandle(window), query, max, scope);
            return ToolResponse.Ok(new
            {
                matches = r.Matches.Select(m => new
                {
                    @ref = m.Ref, automationId = m.AutomationId, name = m.Name, controlType = m.ControlType,
                    bounds = m.Bounds, isOffscreen = m.IsOffscreen, isEnabled = m.IsEnabled, hasFocus = m.HasFocus
                }),
                totalMatches = r.TotalMatches,
                isTruncated = r.IsTruncated
            });
        });
}
