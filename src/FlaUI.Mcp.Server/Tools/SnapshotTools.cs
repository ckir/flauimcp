using System.ComponentModel;
using FlaUI.Mcp.Core.Errors;
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

    [McpServerTool(ReadOnly = true), Description("Diff a window's CURRENT tree against an explicit baseline snapshotId. Returns added/removed/changed (Name/Enabled/Focused) keyed by composite identity (ControlType+AutomationId+RuntimeId, else +Name). Result refs belong to the new currentSnapshotId. Note: anonymous virtualized recycled rows (empty AutomationId+Name, recycled RuntimeId) can collide — diff such content by value/text instead.")]
    public Task<string> DesktopSnapshotDiff(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("REQUIRED baseline snapshotId to diff against, e.g. w1:2.")] string baselineSnapshotId)
        => ToolResponse.Guard(async () =>
        {
            var d = await _perception.DiffAsync(new WindowHandle(window), baselineSnapshotId);
            return ToolResponse.Ok(new
            {
                baselineSnapshotId = d.BaselineSnapshotId, currentSnapshotId = d.CurrentSnapshotId,
                added = d.Added.Select(a => new { @ref = a.Ref, controlType = a.ControlType, automationId = a.AutomationId, name = a.Name }),
                removed = d.Removed.Select(a => new { @ref = a.Ref, controlType = a.ControlType, automationId = a.AutomationId, name = a.Name }),
                changed = d.Changed.Select(c => new { @ref = c.Ref, was = c.Was, now = c.Now })
            });
        });

    [McpServerTool(ReadOnly = true), Description("Poll a window until a selector condition holds. by=automationId|name|controlType, value=target, until=exists|enabled|gone|valueEquals (equals required for valueEquals; compares ValuePattern→Name→LegacyIAccessible). Timeout returns {satisfied:false} (NOT an error). On success returns the matched ref + a fresh snapshotId. Polls are transient (no ref growth).")]
    public Task<string> DesktopWaitFor(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("automationId|name|controlType.")] string by,
        [Description("Match target for 'by'.")] string value,
        [Description("exists|enabled|gone|valueEquals (default exists).")] string until = "exists",
        [Description("Required iff until=valueEquals.")] string? equals = null,
        [Description("Total wait budget ms (default 5000).")] int timeoutMs = 5000,
        [Description("Poll interval ms (default 500).")] int pollIntervalMs = 500)
        => ToolResponse.Guard(async () =>
        {
            var r = await _wait.WaitForAsync(new WindowHandle(window), by, value, until, equals, timeoutMs, pollIntervalMs);
            return ToolResponse.Ok(new { satisfied = r.Satisfied, @ref = r.Ref, elapsedMs = r.ElapsedMs, snapshotId = r.SnapshotId });
        });

    [McpServerTool(ReadOnly = true), Description("Cheap orientation: control counts (total/interactive/offscreen/redacted, FULL tree) + a per-ControlType histogram, without the tree text. Supply exactly one of window (fresh full walk — a fuller view than a pruned desktop_snapshot) or snapshotId (a prior cached snapshot, tallied as-snapshotted).")]
    public Task<string> DesktopSnapshotStats(
        [Description("Window handle. Provide this OR snapshotId.")] string? window = null,
        [Description("A prior snapshotId, e.g. w1:4. Provide this OR window.")] string? snapshotId = null)
        => ToolResponse.Guard(async () =>
        {
            if (string.IsNullOrEmpty(window) == string.IsNullOrEmpty(snapshotId))
                throw new ToolException(ToolErrorCode.InvalidArguments, "Provide exactly one of 'window' or 'snapshotId'.", "pass a window handle or a snapshotId");
            var s = string.IsNullOrEmpty(window) ? _perception.StatsBySnapshotId(snapshotId!) : await _perception.StatsByWindowAsync(new WindowHandle(window!));
            return ToolResponse.Ok(new { snapshotId = s.SnapshotId, total = s.Total, interactive = s.Interactive, offscreen = s.Offscreen, redacted = s.Redacted, byControlType = s.ByControlType });
        });
}
