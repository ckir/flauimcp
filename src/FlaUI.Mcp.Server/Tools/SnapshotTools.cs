using System.ComponentModel;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class SnapshotTools
{
    private readonly PerceptionManager _perception;
    private readonly WaitCoordinator _wait;
    public SnapshotTools(PerceptionManager perception, WaitCoordinator wait)
    { _perception = perception; _wait = wait; }

    [McpServerTool(ReadOnly = true), Description("Walk a window's accessibility tree into an indented, ref-tagged snapshot. " +
        "Each line: [e23] Button \"OK\" @{x,y,w,h} {enabled, focusable} [Invoke]. Use the e-refs with later interaction tools.")]
    public Task<string> DesktopSnapshot(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Optional ref to root the snapshot at (from a prior snapshot of this window).")] string? root = null,
        [Description("Max tree depth (default 40).")] int maxDepth = 40,
        [Description("Prune non-interactive container/decoration noise (default true).")] bool interactiveOnly = true,
        [Description("Append AutomationId/HelpText to each line (default false).")] bool fullProperties = false,
        [Description("Include off-screen (scrolled/virtualized out of view) elements (default false).")] bool includeOffscreen = false)
        => ToolResponse.Guard(async () =>
        {
            var opts = new SnapshotOptions
            {
                RootRef = root, MaxDepth = maxDepth,
                InteractiveOnly = interactiveOnly, FullProperties = fullProperties,
                IncludeOffscreen = includeOffscreen,
            };
            var r = await _perception.SnapshotAsync(new WindowHandle(window), opts);
            return ToolResponse.Ok(new { snapshotId = r.SnapshotId, nodeCount = r.NodeCount, tree = r.Tree });
        });
}
