using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>AGY-TEST-AUDIT gap 2 — the INTEGRATION nobody was testing.
///
/// The audit's finding, verified before this file was written: the only mentions of
/// <see cref="ElementContent"/> anywhere under test/ were ALLOWLIST STRINGS inside
/// RedactionSurfaceInventoryTests. No test invoked it directly at all. So the Q-j2 fold —
/// "an unknowable process must WITHHOLD rather than let unknown read as does-not-apply" —
/// could be deleted from ElementContent.Classify and the entire suite would stay green,
/// because the classifier's predicate was pinned in isolation while the code that ACTS on it
/// was not.
///
/// That is the difference between a behaviour being NAMED by a test and being COVERED by one.</summary>
[Trait("Category", "Desktop")]
public class ElementContentContractTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ElementContentContractTests(TestAppFixture app) => _app = app;

    /// <summary>A rule scoped to a process that is NOT the fixture. It can never match here — the point
    /// is that its mere EXISTENCE makes an unknown process unknowable rather than irrelevant.</summary>
    private static SensitivityClassifier ScopedToSomeoneElse() =>
        SensitivityClassifier.ForRules(new[]
        {
            new RedactionRule("billing", processName: "Contoso.Billing", global: false,
                              automationId: "AcctNumber", automationIdPattern: null, namePattern: null)
        });

    /// <summary>Both directions in ONE fact, deliberately: they are what make each other meaningful.
    ///
    /// A null process name means "a process-scoped rule exists and I cannot tell whether it applies",
    /// so the content is withheld and reports its own provenance. A KNOWN process name that simply does
    /// not match is answerable, so the content is served. Without the second assertion, an implementation
    /// that redacted unconditionally would pass the first.</summary>
    [Fact]
    public async Task ElementContent_withholds_when_the_process_is_unknowable_but_serves_when_it_is_merely_unmatched()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (unknowable, answerable) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var classifier = ScopedToSomeoneElse();
            return (Unknowable: ElementContent.Name(win, classifier, null),
                    Answerable: ElementContent.Name(win, classifier, "notepad"));
        });

        // UNKNOWABLE: withheld, and it says WHY — not "os", not a rule name the operator never wrote.
        Assert.True(unknowable.Sensitivity.Redact);
        Assert.Equal(RedactionSource.Unreadable, unknowable.Sensitivity.Source);
        Assert.Equal(ElementContent.RedactedToken, unknowable.Text);
        Assert.Equal("unreadable", ElementContent.RedactedBy(unknowable.Sensitivity));

        // ANSWERABLE: a known process the rule does not cover is served normally. This is the half that
        // stops the fail-closed from degenerating into "withhold whenever any rule exists anywhere".
        Assert.False(answerable.Sensitivity.Redact);
        Assert.NotEqual(ElementContent.RedactedToken, answerable.Text);

        // ...and the BC-1 identity path is unaffected by the withholding: the raw name is still carried
        // for ref re-resolution even when the text is withheld.
        Assert.NotEqual(ElementContent.RedactedToken, unknowable.RawForIdentity);
    }
}
