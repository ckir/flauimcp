using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Server.Capture;
using FlaUI.Mcp.Server.Tools;

namespace FlaUI.Mcp.Tests;

/// <summary>Builds <see cref="ScreenshotTools"/> for the Desktop tests the way production builds it.
///
/// ⚠⚠ THE POINT IS THE ONE THING THIS DOES NOT LET A CALLER DO: forget a load-bearing delegate.
/// `WindowCaptureCoordinator.ForPerception` wires `denylistedVisible` and `desktopMasks` internally --
/// both are OPTIONAL constructor parameters, and omitting the first is exactly what made round 5's
/// denylist fix INERT in production while every test still passed. Task 20 widened `ScreenshotTools`'
/// constructor and broke FIVE Desktop call sites; each one rewiring this by hand would have been five
/// more chances to drop a guard. Production's DI registration calls the same factory, so the two cannot
/// drift apart.
///
/// ⚠⚠ THE AUDIT SIGNAL IS DISABLED HERE, AND THAT IS CORRECTNESS, NOT TIDINESS. An enabled signal fans
/// out to real channels, and `FlashSignal`/`GdiActionOverlay` draw a REAL TOP-MOST WINDOW on the screen
/// -- `CaptureAuditSignal`'s own summary warns that a signal raised too early "can appear in the
/// captured pixels". These are Desktop tests that assert on captured pixels and redaction counts, so a
/// live signal could corrupt the very image under assertion. Off is also production's default: the flag
/// is opt-in via `--capture-audit-signal`, so this matches a default install rather than diverging from
/// it. Task 21's own suite is where the signal's behaviour is tested, with a recording fake.
///
/// The breaker is a FRESH instance per call rather than a shared one, so a test that trips it for a hung
/// window cannot colour any later test. (`CaptureCircuitBreaker.Default` is an expression-bodied
/// property, so every read already yields a new instance -- this relies on that, and says so, because a
/// future change to `Default` that returned a cached singleton would silently couple these tests.)</summary>
internal static class ScreenshotToolsFactory
{
    public static ScreenshotTools For(PerceptionManager perception) =>
        new(perception,
            WindowCaptureCoordinator.ForPerception(perception,
                                                   new PrintWindowImageSource(),
                                                   CaptureCircuitBreaker.Default),
            new CaptureAuditSignal(NullAttentionSignal.Instance, enabled: false));
}
