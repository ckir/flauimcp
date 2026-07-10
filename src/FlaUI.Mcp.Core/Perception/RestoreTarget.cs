namespace FlaUI.Mcp.Core.Perception;

/// <summary>Pure decision for restoring the originally-active terminal tab (spec §5.2.9). You cannot
/// restore by a pre-switch ref (stale — §3.4) or by AutomationId (none — §5.2.7). Title is the only
/// pre-verifiable identity; ordinal is the fallback. Restore is best-effort with honest reporting.</summary>
public static class RestoreTarget
{
    public readonly record struct Result(int? SelectIndex, bool Restored, string Confidence);

    /// <param name="recordedTitle">Title of the tab that was active up front.</param>
    /// <param name="recordedOrdinal">Its 0-based index in the up-front enumeration.</param>
    /// <param name="wasTitleUnique">Whether that title was unique in the up-front enumeration.</param>
    /// <param name="freshTitles">Titles re-enumerated at restore time, in ordinal order.</param>
    public static Result Resolve(string recordedTitle, int recordedOrdinal, bool wasTitleUnique,
        IReadOnlyList<string> freshTitles)
    {
        // Prefer a confident title match: only when the title WAS unique up front AND still resolves to
        // exactly one tab now (a concurrent add could duplicate it — then it is no longer confident).
        if (wasTitleUnique)
        {
            int found = -1, count = 0;
            for (int i = 0; i < freshTitles.Count; i++)
                if (string.Equals(freshTitles[i], recordedTitle, System.StringComparison.Ordinal))
                { found = i; count++; }
            if (count == 1) return new Result(found, true, "high");
        }
        // Ordinal fallback (ambiguous title up front, or unique-but-not-uniquely-found now).
        if (recordedOrdinal >= 0 && recordedOrdinal < freshTitles.Count)
            return new Result(recordedOrdinal, true, "reduced");
        // Count shrank / ordinal out of range and no confident title: cannot restore — report honestly.
        return new Result(null, false, "none");
    }
}
