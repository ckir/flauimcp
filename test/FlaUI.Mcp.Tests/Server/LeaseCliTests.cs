using System;
using System.IO;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Collection("LeaseEnv")]   // shares the LeaseEnv collection (defined in FileLeaseProviderTests) to serialize env-var mutation
public class LeaseCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-leasecli-" + Guid.NewGuid().ToString("N"));
    public LeaseCliTests() => Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", _dir);
    public void Dispose() { Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", null); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Unlock_is_a_recognized_verb()
        => Assert.True(CliRouter.IsInstallerVerb(new[] { "unlock" }));

    [Fact]
    public void Unlock_writes_a_valid_future_lease_then_lock_removes_it()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "unlock", "--minutes", "5", "--allow-shells" }, "exe", outp);

        var lease = new FileLeaseProvider().Read(out _);
        Assert.NotNull(lease);
        Assert.True(lease!.ExpiryUtc > DateTime.UtcNow.AddMinutes(4));
        Assert.True(lease.HasCapability("shells"));

        CliRouter.Run(new[] { "lock" }, "exe", outp);
        Assert.Null(new FileLeaseProvider().Read(out _));
    }
}
