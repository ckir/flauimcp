using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>SP3 Task 12 step 4c — PIN THE DI ASSUMPTION. Do not leave this as anyone's reasoning.
///
/// Task 4b had to make `classifier` an OPTIONAL constructor parameter
/// (`SensitivityClassifier? classifier = null`, defaulting to OsOnly) because ~35 pre-existing test files
/// construct PerceptionManager directly and §4.4 forbids editing them. Production relies on Microsoft DI
/// injecting the REGISTERED singleton (Program.cs:96 `AddSingleton(classifier)`) into that parameter
/// instead of falling back to the default.
///
/// ⚠ If that assumption is FALSE, the server runs with SensitivityClassifier.OsOnly — every operator rule
/// silently stops applying, the whole per-field redaction feature is INERT in production, and every other
/// test in this suite still passes. That is the failure mode this fact exists to make impossible.
///
/// Assert.Same, not Assert.True(HasRules): IDENTITY is the claim being pinned.
/// Headless — a ServiceCollection needs no window.</summary>
public class ClassifierDiPinTests
{
    [Fact]
    public void Di_injects_the_registered_classifier_not_the_parameter_default()
    {
        var registered = SensitivityClassifier.ForRules(new[]
        {
            new RedactionRule("di-pin", processName: null, global: true,
                              automationId: null, automationIdPattern: null, namePattern: "^recognisable$")
        });

        var services = new ServiceCollection();
        services.AddSingleton(registered);                 // mirrors Program.cs:96
        services.AddSingleton<AutomationDispatcher>();     // mirrors Program.cs:110-116
        services.AddSingleton<WindowManager>();
        services.AddSingleton<RefRegistry>();
        services.AddSingleton<SnapshotCache>();
        services.AddSingleton<PerceptionManager>();

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<PerceptionManager>();

        Assert.Same(registered, resolved.Classifier);
        Assert.NotSame(SensitivityClassifier.OsOnly, resolved.Classifier);
        Assert.True(resolved.Classifier.HasRules);
    }
}
