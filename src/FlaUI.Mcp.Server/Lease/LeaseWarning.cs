namespace FlaUI.Mcp.Server.Lease;

public enum LeaseWarningDecision { NoWarning, ProceedWithLoggedWarning, RefuseNeedsAck }

/// <summary>Pure policy for the long-lease disclaiming warning (spec §4.6). Threshold: > 60 minutes.</summary>
public static class LeaseWarning
{
    public const int ThresholdMinutes = 60;

    public static LeaseWarningDecision Decide(int minutes, bool hasAcceptFlag, bool isInteractive)
    {
        if (minutes <= ThresholdMinutes) return LeaseWarningDecision.NoWarning;
        if (hasAcceptFlag || isInteractive) return LeaseWarningDecision.ProceedWithLoggedWarning;
        return LeaseWarningDecision.RefuseNeedsAck;
    }

    public static string Text(int minutes) =>
        $"WARNING: You are granting an uncontained {minutes}-minute input lease. FlaUI.Mcp provides NO " +
        "sandboxing or protection during this window. A prompt-injected agent can take full control of this " +
        "machine and your credentials. Only an ephemeral VM or low-privilege guest account can contain this " +
        "risk. Type 'I understand' to continue.";
}
