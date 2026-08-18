using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Walks a UIA subtree into an indented, ref-tagged text snapshot. Stateless;
/// registers each surfaced element into the supplied RefRegistry. Runs on the caller's
/// thread — callers MUST invoke it on the query STA (see WindowManager primitive).</summary>
public static class SnapshotEngine
{
    // Curated "meaningful" roles surfaced under interactiveOnly. Containers/decoration are
    // pruned from OUTPUT but still recursed THROUGH so their interactive descendants appear.
    private static readonly HashSet<ControlType> InteractiveTypes = new()
    {
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit,
        ControlType.Hyperlink, ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton,
        ControlType.Slider, ControlType.Spinner, ControlType.SplitButton, ControlType.Tab,
        ControlType.TabItem, ControlType.TreeItem, ControlType.Document, ControlType.List,
        ControlType.Menu, ControlType.Tree, ControlType.DataGrid, ControlType.Table,
    };

    public static (string Tree, int NodeCount) Walk(
        AutomationElement root,
        IReadOnlyList<AutomationElement> popupRoots,
        SnapshotOptions options,
        RefRegistry refs,
        string windowId)
    {
        // SP3 Rule 4b: pass the classifier EXPLICITLY rather than relying on Build's optional default.
        // OsOnly is CORRECT here and is a stated decision, not an oversight: Walk has no src/ callers
        // (verified) - it is a convenience wrapper over Build+Render kept for pre-existing tests, which
        // §4.4 forbids editing. The whole point of 4b is that a SILENT fallback to OsOnly is invisible;
        // naming it makes the choice reviewable.
        // ⚠ If Walk ever becomes reachable from production, this OsOnly is a real leak - operator rules
        // would stop applying to everything it renders. Thread a real classifier through at that point.
        var model = Build(root, popupRoots, options, refs, windowId, SensitivityClassifier.OsOnly);
        return (Render(model, options), model.NodeCount);
    }

    public static SnapshotModel Build(
        AutomationElement root, IReadOnlyList<AutomationElement> popupRoots,
        SnapshotOptions options, RefRegistry refs, string windowId,
        SensitivityClassifier? classifier = null, string? processName = null)
    {
        var items = new List<SnapshotItem>();
        var popupRids = new List<int[]>();
        foreach (var p in popupRoots)
        {
            var prid = Safe(() => p.Properties.RuntimeId.ValueOrDefault, (int[]?)null);
            if (prid != null) popupRids.Add(prid);
        }
        var rootBounds = Safe(() => root.BoundingRectangle, System.Drawing.Rectangle.Empty);
        Visit(root, 0, Array.Empty<int>(), null, "", rootBounds, classifier, processName);
        if (popupRoots.Count > 0)
        {
            items.Add(new OverlaysHeaderItem());
            for (int i = 0; i < popupRoots.Count; i++)
            {
                var pb = Safe(() => popupRoots[i].BoundingRectangle, System.Drawing.Rectangle.Empty);
                Visit(popupRoots[i], 0, new[] { -1 - i }, null, "  ", pb, classifier, processName);
            }
        }
        return new SnapshotModel(items);

        // SP3 Task 4b: classifier/processName are threaded through but UNUSED here — Tasks 5-9 make the
        // redaction decision. Unused PARAMETERS (unlike fields) do not trip CS0169/CS0414.
        void Visit(AutomationElement el, int depth, int[] indexPath, string? ancestorAid, string indent,
            System.Drawing.Rectangle cullBounds, SensitivityClassifier? nodeClassifier, string? nodeProcessName)
        {
            int[] rid = Safe(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? Array.Empty<int>();
            if (depth > 0)
                foreach (var prid in popupRids)
                    if (RidEqual(rid, prid)) return;
            if (depth > 0 && !options.IncludeOffscreen && Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false)) return;
            if (depth > 0 && !options.IncludeOffscreen && options.CullToWindowBounds && cullBounds.Width > 0 && cullBounds.Height > 0)
            {
                var rect0 = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                if (rect0.Width <= 0 || rect0.Height <= 0 || !rect0.IntersectsWith(cullBounds)) return;
            }
            // ⚠ CAPSTONE ROUND 3 (finding S1). These two reads are the ones a redaction RULE matches on, so
            // a swallowed throw here is not cosmetic: "" matches no rule, the element is emitted Visible,
            // and its content reaches the wire. The L2 fix closed exactly this hole in
            // ElementContent.Classify — but THIS walk never calls that method (see the classifier call
            // below), so the snapshot tree, the largest wire surface of all, was still failing OPEN.
            string aid = Safe(() => el.AutomationId, "", out bool aidThrew);
            ControlType ct = Safe(() => el.ControlType, ControlType.Custom);
            string name = Safe(() => el.Name, "", out bool nameThrew);
            bool include = depth == 0 || !options.InteractiveOnly || IsInteresting(el, ct, name);
            string childIndent = indent;
            if (include)
            {
                var rect = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                bool enabled = Safe(() => el.IsEnabled, false);
                bool focusable = Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false);
                bool focused = Safe(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false);
                bool selected = Safe(() => el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault ?? false, false);
                Sensitivity sensitivity = nodeClassifier is not null && nodeClassifier.HasRules
                    ? nodeClassifier.Classify(nodeProcessName, () => aid, () => name,
                                          () => el.Properties.IsPassword.ValueOrDefault)
                    : (RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault)
                        ? Sensitivity.OsPassword : Sensitivity.Visible);
                // FAIL CLOSED when a rule COULD have matched but the identity it matches on was unreadable.
                // Gated on HasRules, so the default path (spec §4.4) is byte-identical to before: with no
                // rules there is no rule to fail closed for, and OS passwords are already fail-closed above.
                //
                // ⚠ Deliberately BROADER than ElementContent.Classify's version, and the difference is a
                // property of this walk, not an oversight: there the thunks are lazy, so only a read a rule
                // actually asked for can trip it. Here both identity reads happen EAGERLY for every node
                // (the walk needs them anyway), so this cannot tell whether a rule would have consulted the
                // one that threw. It therefore withholds on either — the conservative direction, and it
                // fires only when a UIA read genuinely throws AND an operator has configured rules.
                if (!sensitivity.Redact && nodeClassifier is not null && nodeClassifier.HasRules
                    && (aidThrew || nameThrew))
                    sensitivity = Sensitivity.UnreadableIdentity;
                bool offscreen = Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false);
                var patterns = SupportedPatterns(el);
                string help = Safe(() => el.HelpText, "");
                // The RAW name is deliberate and LOAD-BEARING: RefRegistry.ResolveDescriptor falls back to
                // Name+ControlType when AutomationId is absent, and RefRegistry.FastPathMatches compares
                // it on the cached fast path, so redacting it here would make a sensitive element with no
                // AutomationId permanently REF_STALE_UNRESOLVABLE. Redaction happens at every WIRE surface
                // instead (SnapshotEngine.Render, WaitCoordinator.Matches, SnapshotDiff.ShownName,
                // FindQuery, WatchPayloadBuilder) and RefRegistry.Key never echoes it.
                var descriptor = new ElementDescriptor(rid, ct, aid, name, ancestorAid, indexPath, focused);
                var @ref = refs.Register(windowId, descriptor, el);
                items.Add(new SnapshotNode(@ref, depth, indent, ct, aid, name, rect, enabled, focusable,
                    focused, selected, sensitivity, offscreen, rid, patterns, help));
                childIndent = indent + "  ";
            }
            var nextAncestor = string.IsNullOrEmpty(aid) ? ancestorAid : aid;
            AutomationElement[] children = Safe(() => el.FindAllChildren(), Array.Empty<AutomationElement>());
            if (depth >= options.MaxDepth)
            {
                if (children.Length > 0) items.Add(new DepthLimitItem(childIndent, children.Length, options.MaxDepth));
                return;
            }
            for (int i = 0; i < children.Length; i++)
            {
                var nextPath = new int[indexPath.Length + 1];
                Array.Copy(indexPath, nextPath, indexPath.Length);
                nextPath[^1] = i;
                Visit(children[i], depth + 1, nextPath, nextAncestor, childIndent, cullBounds, nodeClassifier, nodeProcessName);
            }
        }
    }

    public static string Render(SnapshotModel model, SnapshotOptions options)
    {
        var sb = new StringBuilder();
        foreach (var item in model.Items)
            switch (item)
            {
                case SnapshotNode n: sb.AppendLine(FormatNode(n, options)); break;
                case OverlaysHeaderItem: sb.AppendLine("[Active Overlays]"); break;
                case DepthLimitItem d:
                    sb.Append(d.Indent).Append("… ").Append(d.MoreCount)
                      .Append(" more (depth limit ").Append(d.MaxDepth).AppendLine(")");
                    break;
            }
        return sb.ToString();
    }

    private static string FormatNode(SnapshotNode n, SnapshotOptions options)
    {
        var state = new List<string>();
        if (n.Enabled) state.Add("enabled");
        if (n.Focusable) state.Add("focusable");
        if (n.Focused) state.Add("focused");
        if (n.Selected) state.Add("selected");
        if (n.Sensitivity.Source == RedactionSource.Rule) state.Add($"redacted:rule:{n.Sensitivity.RuleName}");
        // Fail-closed, identity unreadable (capstone L2). Marked so an operator can tell this apart from a
        // rule they wrote; an OS password still carries no marker, only the [REDACTED] name.
        else if (n.Sensitivity.Source == RedactionSource.Unreadable) state.Add("redacted:unreadable");
        string shownName = n.Sensitivity.Redact ? "[REDACTED]" : n.Name;
        var sb = new StringBuilder();
        sb.Append(n.Indent).Append('[').Append(n.Ref).Append("] ").Append(n.ControlType).Append(' ')
          .Append('"').Append(shownName).Append('"')
          .Append(" @{").Append(n.Bounds.X).Append(',').Append(n.Bounds.Y).Append(',')
          .Append(n.Bounds.Width).Append(',').Append(n.Bounds.Height).Append('}')
          .Append(" {").Append(string.Join(", ", state)).Append('}');
        if (n.Patterns.Count > 0) sb.Append(" [").Append(string.Join(",", n.Patterns)).Append(']');
        if (options.FullProperties)
            sb.Append(" aid=").Append(n.AutomationId).Append(" help=\"").Append(n.HelpText).Append('"');
        return sb.ToString();
    }

    /// <summary>Node-level counterpart to IsInteresting, for post-hoc stats over a SnapshotModel.
    /// Mirrors the same logic without needing a live AutomationElement.</summary>
    public static bool IsInteractiveNode(SnapshotNode n)
        => InteractiveTypes.Contains(n.ControlType)
           || (n.ControlType == ControlType.Text && !string.IsNullOrWhiteSpace(n.Name))
           || n.Focusable || n.Patterns.Count > 0;

    private static bool IsInteresting(AutomationElement el, ControlType ct, string name)
    {
        if (InteractiveTypes.Contains(ct)) return true;
        if (ct == ControlType.Text && !string.IsNullOrWhiteSpace(name)) return true; // named labels inform
        if (Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false)) return true;
        // any actionable pattern makes it interesting
        return SupportedPatterns(el).Length > 0;
    }

    private static string[] SupportedPatterns(AutomationElement el)
    {
        var p = el.Patterns;
        var checks = new (string Name, Func<bool> Supported)[]
        {
            ("Invoke", () => p.Invoke.IsSupported),
            ("Value", () => p.Value.IsSupported),
            ("Toggle", () => p.Toggle.IsSupported),
            ("ExpandCollapse", () => p.ExpandCollapse.IsSupported),
            ("Selection", () => p.Selection.IsSupported),
            ("SelectionItem", () => p.SelectionItem.IsSupported),
            ("ScrollItem", () => p.ScrollItem.IsSupported),
            ("Scroll", () => p.Scroll.IsSupported),
            ("Grid", () => p.Grid.IsSupported),
            ("Text", () => p.Text.IsSupported),
            ("Window", () => p.Window.IsSupported),
            ("Transform", () => p.Transform.IsSupported),
        };
        return checks.Where(c => Safe(c.Supported, false)).Select(c => c.Name).ToArray();
    }

    // Zero-allocation RuntimeId equality (UIA RuntimeIds are small int[]).
    internal static bool RidEqual(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    /// <summary>As <see cref="Safe{T}(Func{T}, T)"/>, but reports whether the read THREW. Used only for the
    /// two IDENTITY reads a redaction rule can match on, so the walk can fail closed instead of matching
    /// rules against a fallback value the element never actually had. See the L2 note at the call site.</summary>
    private static T Safe<T>(Func<T> read, T fallback, out bool threw)
    {
        try { threw = false; return read(); }
        catch { threw = true; return fallback; }
    }

    /// <summary>Walk parents to the first ancestor carrying a non-empty AutomationId (the "nearest
    /// stable ancestor" - the same value snapshot's Visit threads top-down, so a find-built descriptor
    /// scopes re-resolution identically to a snapshot-built one). Bounded by the tree; a null walker
    /// or a read failure ends the walk (returns null = no stable ancestor scope).</summary>
    public static string? NearestAncestorAutomationId(AutomationElement el)
    {
        try
        {
            var walker = el.Automation.TreeWalkerFactory.GetRawViewWalker();
            var cur = Safe(() => walker.GetParent(el), (AutomationElement?)null);
            while (cur is not null)
            {
                var aid = Safe(() => cur.AutomationId, "");
                if (!string.IsNullOrEmpty(aid)) return aid;
                cur = Safe(() => walker.GetParent(cur!), (AutomationElement?)null);
            }
        }
        catch { /* fall through */ }
        return null;
    }
}
