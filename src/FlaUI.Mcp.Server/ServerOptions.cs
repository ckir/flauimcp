namespace FlaUI.Mcp.Server;

/// <summary>Process-wide server options parsed from argv. ReadOnly rejects every
/// non-read-only tool, independent of whether the MCP client honors destructiveHint.</summary>
public sealed record ServerOptions(bool ReadOnly)
{
    public static ServerOptions FromArgs(string[] args) =>
        new(ReadOnly: args.Contains("--read-only-mode"));
}
