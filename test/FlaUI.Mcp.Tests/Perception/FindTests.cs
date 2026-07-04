// test/FlaUI.Mcp.Tests/Perception/FindTests.cs
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class FindTests
{
    private static async Task<(WindowManager mgr, PerceptionManager perception, WindowHandle handle)>
        OpenAsync(TestAppFixture app, AutomationDispatcher dispatcher)
    {
        var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var perception = new PerceptionManager(mgr, refs, new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        return (mgr, perception, handle);
    }

    [Fact]
    public async Task Find_by_automationId_returns_one_match_with_ref_and_bounds()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var r = await perception.FindAsync(handle,
                new FindQuery("OkButton", null, "eq", null, false), max: 20, scopeRef: null);

            var m = Assert.Single(r.Matches);
            Assert.StartsWith("e", m.Ref);
            Assert.Equal("OkButton", m.AutomationId);
            Assert.Equal(4, m.Bounds.Length);
            Assert.True(m.IsEnabled);
            Assert.Equal(1, r.TotalMatches);
            Assert.False(r.IsTruncated);
        }
    }

    [Fact]
    public async Task Find_by_name_contains_matches_substring()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var r = await perception.FindAsync(handle,
                new FindQuery(null, "Item", "contains", "ListItem", false), max: 20, scopeRef: null);
            Assert.True(r.Matches.Count >= 2); // ItemA, ItemB (at least)
            Assert.All(r.Matches, m => Assert.Equal("ListItem", m.ControlType));
        }
    }

    [Fact]
    public async Task Find_unknown_controlType_throws_InvalidArguments()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var ex = await Assert.ThrowsAsync<ToolException>(() => perception.FindAsync(handle,
                new FindQuery(null, null, "eq", "NotARealType", false), 20, null));
            Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
        }
    }

    [Fact]
    public async Task Find_truncates_and_reports_totalMatches()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var r = await perception.FindAsync(handle,
                new FindQuery(null, "Item", "contains", "ListItem", false), max: 1, scopeRef: null);
            Assert.Single(r.Matches);
            Assert.True(r.TotalMatches >= 2);
            Assert.True(r.IsTruncated);
        }
    }

    [Fact]
    public async Task Find_is_additive_prior_snapshot_ref_survives()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            var okRef = RefLineHelper.RefFor(snap.Tree, "OkButton");

            await perception.FindAsync(handle, new FindQuery("OkButton", null, "eq", null, false), 20, null);

            // The snapshot ref is STILL resolvable (find did NOT BeginSnapshot / supersede it).
            var aid = await perception.RunOnRefAsync(handle, okRef, el => el.AutomationId);
            Assert.Equal("OkButton", aid);
        }
    }

    [Fact]
    public async Task Find_no_match_returns_empty_not_error()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var r = await perception.FindAsync(handle,
                new FindQuery("NoSuchControl_zzz", null, "eq", null, false), 20, null);
            Assert.Empty(r.Matches);
            Assert.Equal(0, r.TotalMatches);
            Assert.False(r.IsTruncated);
        }
    }

    [Fact]
    public async Task Find_password_field_returns_redacted_name_and_never_leaks_the_secret()
    {
        // TestApp's PasswordBox has AutomationId "Secret" and typed value "hunter2-NEVER-LEAK"
        // (mirrors MainWindow.SecretValue; see PasswordRedactionTests). find must (a) redact the
        // returned name of the IsPassword element to "[REDACTED]", and (b) never surface the secret
        // VALUE in any match (find reads identity, not ValuePattern; and a password element's name is
        // redacted BEFORE the match decision -> no name-oracle, INV-5).
        const string Secret = "hunter2-NEVER-LEAK";
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var pwd = await perception.FindAsync(handle,
                new FindQuery("Secret", null, "eq", null, false), 20, null);
            var m = Assert.Single(pwd.Matches);
            Assert.Equal("[REDACTED]", m.Name); // output redaction (isPwd -> "[REDACTED]")

            // A broad find never carries the secret value in ANY element's name.
            var all = await perception.FindAsync(handle, new FindQuery(null, null, "eq", null, false), 500, null);
            Assert.False(all.IsTruncated); // else a dropped element could make DoesNotContain pass trivially
            Assert.DoesNotContain(all.Matches, x => x.Name.Contains(Secret));

            // Query by the secret as a name -> no match.
            var byName = await perception.FindAsync(handle, new FindQuery(null, Secret, "contains", null, false), 50, null);
            Assert.Empty(byName.Matches);
        }
    }

    // NOTE: this pins output redaction + no-secret-leak. Fully ISOLATING the redact-before-match path
    // (a password element whose NAME itself equals the query) would need a TestApp control whose
    // IsPassword Name is sensitive - an optional small TestApp addition, out of scope for v0.7.3.

    [Fact]
    public async Task Find_minted_ref_re_resolves_after_tree_mutation()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var r = await perception.FindAsync(handle,
                new FindQuery("ItemB", null, "eq", null, false), 20, null);
            var refB = Assert.Single(r.Matches).Ref;

            // Rebuild destroys+recreates ItemB -> the cached element dies; the descriptor must re-bind.
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            {
                win.FindFirstDescendant(cf => cf.ByAutomationId("RebuildItemsButton"))!.AsButton().Invoke();
                return true;
            });
            await Task.Delay(300);

            var aid = await perception.RunOnRefAsync(handle, refB, el => el.AutomationId);
            Assert.Equal("ItemB", aid); // find's descriptor is as durable as snapshot's (Task 2 parity)
        }
    }

    [Fact]
    public async Task Find_ignoreCase_eq_matches_across_case()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            // OkButton's Name (from Content="OK") is uppercase; an ordinal eq would reject the
            // lowercase query. IgnoreCase must NOT push the name into the native UIA condition
            // (Task 2) and instead let the managed post-filter (OrdinalIgnoreCase) match it.
            var r = await perception.FindAsync(handle,
                new FindQuery(null, "ok", "eq", null, false, IgnoreCase: true), max: 20, scopeRef: null);
            var m = Assert.Single(r.Matches);
            Assert.Equal("OkButton", m.AutomationId);
        }
    }

    [Fact]
    public async Task Find_ignoreCase_contains_matches_multiple_across_case()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            // "Rebuild Items" / "Clear Items" carry uppercase "Items"; an ordinal contains("items")
            // would miss both. IgnoreCase must catch them via the post-filter.
            var r = await perception.FindAsync(handle,
                new FindQuery(null, "items", "contains", null, false, IgnoreCase: true), max: 20, scopeRef: null);
            Assert.True(r.TotalMatches >= 2);
            Assert.Contains(r.Matches, m => m.AutomationId == "RebuildItemsButton");
            Assert.Contains(r.Matches, m => m.AutomationId == "ClearItemsButton");
        }
    }
}
