using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Install;

/// <summary>The single place the activation core is attached to the MCP server.
///
/// This exists as a NAMED METHOD, not an inline lambda in Program.cs, for one reason: a test can call it
/// against a real McpServerOptions and assert what it sets. Program.cs is top-level statements that build
/// and immediately run a stdio host, so a test cannot otherwise observe this configuration - the earlier
/// alternative was grepping Program.cs, and a source sweep can be satisfied by a string literal while the
/// real wiring is gone.</summary>
public static class McpServerConfiguration
{
    /// <summary>Serve the CORE, never Text: Text carries Claude Code's ToolSearch incantation and the
    /// driving-flaui-mcp pointer, which mean nothing to a client that has neither (spec D1).</summary>
    public static void Apply(McpServerOptions options)
        => options.ServerInstructions = ActivationPayload.Core;
}
