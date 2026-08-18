using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP3 Task 9 — DEF-3 on the FIND path, plus the BC-1 targetability proof (plan Step 4b).
///
/// The fixture element these facts turn on is `&lt;ListBoxItem Content="NamedOnly"/&gt;`
/// (MainWindow.xaml:55) — the ONLY item in that list with NO AutomationId. That is precisely the BC-1
/// hazard: its Name is its only searchable identity, so withholding name search is exactly the case
/// where an element could become unreachable. This is the hazard that RETIRED roadmap item 7.
///
/// The rule is declared Global so it does not depend on the fixture's process name.</summary>
[Trait("Category", "Desktop")]
public class RedactionFindOracleTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public RedactionFindOracleTests(TestAppFixture app) => _app = app;

    private const string Target = "NamedOnly";

    private static SensitivityClassifier RedactNamedOnly() =>
        SensitivityClassifier.ForRules(new[]
        {
            new RedactionRule("bc1", processName: null, global: true,
                              automationId: null, automationIdPattern: null, namePattern: "^NamedOnly$")
        });

    private static FindQuery ByName(string name) => new(null, name, "eq", null, false);

    /// <summary>⚠ MEASURED, and the whole reason this helper exists separately from <see cref="ByName"/>.
    /// An "eq" name query is pushed down into UIA as a NATIVE condition, and the native condition matches
    /// the element's RAW name — so a redacted element is filtered out by UIA before the managed post-filter
    /// (where DEF-3 lived) ever runs. A token query with mode "eq" therefore returns nothing even on the
    /// UNFIXED code, and a pin written that way is VACUOUS.
    ///
    /// "contains" has no native pushdown (UIA ByName is exact-only — FindQuery.cs:60-64), so it is the
    /// mode that actually reaches the post-filter, and the only mode by which the find-path oracle was
    /// reachable. Verified by running this file against the pre-fix tree: with "eq" all four facts passed
    /// unfixed; with "contains" the token fact goes red.</summary>
    private static FindQuery ByNameContains(string name) => new(null, name, "contains", null, false);

    private static FindQuery ByControlType(string ct) => new(null, null, "eq", ct, false);

    private async Task<(PerceptionManager P, WindowHandle H)> Fixture(WindowManager mgr)
        => (new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache(), RedactNamedOnly()),
            await mgr.OpenByPidAsync(_app.Process.Id));

    /// <summary>⚠ HONEST LABEL, corrected by the AGY-TEST-AUDIT: this is a TOTAL-REDACTION-FAILURE guard,
    /// NOT a DEF-3 red→green pin. It was previously captioned as the latter, which overstated it.
    ///
    /// VERIFIED against the pre-fix tree: this fact PASSED there too, so it cannot distinguish fixed from
    /// unfixed. The reason is that pre-fix, the managed post-filter compared the query against the
    /// ALREADY-REDACTED name (PerceptionManager's pre-fix comment says so explicitly), so searching
    /// "NamedOnly" was compared against "[REDACTED]", missed, and returned empty — the same empty this
    /// asserts today.
    ///
    /// ⚠ AND SWITCHING TO "contains" WOULD NOT FIX THAT — the audit suggested it and the suggestion is
    /// wrong: `"[REDACTED]".Contains("NamedOnly")` is false pre-fix as well. NO real-name query can go red
    /// pre-fix, because pre-fix already withheld the real name from matching. The redirection is inherent
    /// to the direction of the test, not to the query mode.
    ///
    /// What it DOES guard, and why it stays: if redaction ever failed outright — the raw name reaching the
    /// post-filter unredacted — this goes red. That is a real regression target, just a different (and
    /// larger) one than DEF-3. The genuine DEF-3 red→green evidence is the TOKEN fact below, which uses
    /// "contains" deliberately so it reaches the post-filter at all.</summary>
    [Fact]
    public async Task A_rule_redacted_elements_real_name_never_reaches_the_match_predicate()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (p, h) = await Fixture(mgr);

        var r = await p.FindAsync(h, ByName(Target), 50, null);

        Assert.Empty(r.Matches);
    }

    /// DEF-3 HALF 1 on the find path — nor by the TOKEN. THIS IS THE ONE THAT GOES RED: pre-fix the
    /// post-filter compared the already-redacted name, so a "contains" query for the token enumerated the
    /// ref and bounds of every redacted element in the window. Must use "contains" — see ByNameContains
    /// for why the "eq" spelling of this same fact is vacuous.
    [Fact]
    public async Task A_rule_redacted_element_is_not_findable_by_the_redaction_token()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (p, h) = await Fixture(mgr);

        var r = await p.FindAsync(h, ByNameContains(ElementContent.RedactedToken), 50, null);

        Assert.Empty(r.Matches);
    }

    /// <summary>⚠ DEF-3 WAS REACHABLE IN THE DEFAULT SHIPPING CONFIGURATION — no redaction rules, no opt-in,
    /// just the OS password flag that has always been on. MEASURED against the pre-fix tree, this exact
    /// query returned the TestApp's password box:
    ///
    ///   FindMatch { Ref = e1, AutomationId = Secret, Name = [REDACTED], ControlType = Edit, Bounds = ... }
    ///
    /// That reframes DEF-3: it is not a hazard that arrives with SP3's new rule feature, it is a defect
    /// every existing install already had. Worth keeping as its own fact, because the rule-driven facts
    /// above would all still pass if someone re-opened the oracle for OS passwords only.
    ///
    /// The second assertion is the anti-over-redaction half (BC-2): closing the oracle must not break
    /// "contains" name search generally. Note this does NOT prove the query is un-BANNED — that needs a
    /// VISIBLE element literally named "[REDACTED]", which this fixture has none of; that property is
    /// pinned headlessly instead (WaitNameOracleDef3Tests.A_visible_element_genuinely_named_the_token_
    /// is_still_matched).</summary>
    [Fact]
    public async Task The_oracle_was_open_in_the_default_config_and_is_now_closed()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var noRules = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var h = await mgr.OpenByPidAsync(_app.Process.Id);

        var token = await noRules.FindAsync(h, ByNameContains(ElementContent.RedactedToken), 50, null);
        var ordinary = await noRules.FindAsync(h, ByNameContains("DupName"), 50, null);

        Assert.Empty(token.Matches);        // the OS password box is no longer enumerable by the token
        Assert.NotEmpty(ordinary.Matches);  // BC-2: ordinary "contains" search still works
    }

    /// The exclusion must be per-ELEMENT, not a blanket suppression of name search. An unredacted
    /// element with a perfectly ordinary name is still findable. Without this fact, an implementation
    /// that simply broke name search would pass both facts above.
    [Fact]
    public async Task An_unredacted_element_is_still_findable_by_name()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (p, h) = await Fixture(mgr);

        var r = await p.FindAsync(h, ByName("DupName"), 50, null);

        Assert.NotEmpty(r.Matches);
    }

    /// <summary>FINAL AGY-CAPSTONE, finding L1 — a TRUE red→green pin, and the most serious defect found on
    /// this branch. `EvaluateSelectorValueAsync` backs desktop_wait_for(until:"valueEquals"). Its redaction
    /// gate was the OS IsPassword read ALONE, so an operator RULE never reached it: a rule-redacted element
    /// fell through to the raw Value / Name / LegacyIAccessible reads and the tool became a CONFIRMATION
    /// ORACLE against exactly the fields a rule was written to protect. No secret crosses the wire —
    /// confirming a guess IS the attack, which that method's own comment already said about passwords.
    ///
    /// MEASURED against the pre-fix tree: this returned "NamedOnly", so a caller could confirm the name of
    /// a redacted element by polling guesses. Post-fix it returns null, and because `equals` is required to
    /// be non-null, valueEquals can never satisfy on a redacted element.
    ///
    /// ⚠ Found by an independent review AFTER the full headless suite, the full Desktop suite and the
    /// Task-12 source sweep were all green — the sweep was silenced on this exact member by an allowlist
    /// entry whose stated reason was false. Both halves are fixed; see RedactionSurfaceInventoryTests.</summary>
    [Fact]
    public async Task A_rule_redacted_elements_value_is_not_confirmable_through_wait_for()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (p, h) = await Fixture(mgr);

        var (found, value) = await p.EvaluateSelectorValueAsync(h, "name", Target);

        // Found stays TRUE: withholding the value must not also make the element vanish, or a caller
        // could distinguish "redacted" from "absent" and BC-1 targetability would break.
        Assert.True(found);
        Assert.Null(value);
    }

    /// <summary>The anti-over-redaction half (BC-2). Closing the oracle must not blind valueEquals to
    /// ordinary elements — without this, an implementation that simply returned null for everything would
    /// pass the fact above.</summary>
    [Fact]
    public async Task An_unredacted_elements_value_is_still_readable_through_wait_for()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (p, h) = await Fixture(mgr);

        var (found, value) = await p.EvaluateSelectorValueAsync(h, "name", "DupName");

        Assert.True(found);
        Assert.NotNull(value);
    }

    /// BC-1 TARGETABILITY — plan Step 4b, which the plan requires be PROVEN, not asserted.
    /// The redacted element has NO AutomationId, so name search was its only lookup key. It must still
    /// be reachable by a NON-name query, carry a usable ref, and resolve through that ref — the
    /// descriptor keeps the RAW name internally (BC-1), so re-resolution works even though name SEARCH
    /// is withheld. If this fails, SP3 has reintroduced the defect that invalidated roadmap item 7.
    [Fact]
    public async Task A_rule_redacted_element_without_an_automationId_is_still_targetable_by_ref()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (p, h) = await Fixture(mgr);

        // Reachable WITHOUT a name constraint: controlType carries no content, so it is not withheld.
        var items = await p.FindAsync(h, ByControlType("ListItem"), 50, null);
        var redacted = items.Matches.SingleOrDefault(
            m => m.Name == ElementContent.RedactedToken && m.AutomationId.Length == 0);

        Assert.NotNull(redacted);                    // still enumerable => still targetable
        Assert.NotEqual(0, redacted!.Bounds[2]);     // a real, actionable rect

        // ...and the ref RESOLVES: the stored descriptor kept the raw name, so re-resolution succeeds.
        var ct = await p.RunOnRefAsync(h, redacted.Ref, el => el.ControlType.ToString());
        Assert.Equal("ListItem", ct);
    }
}
