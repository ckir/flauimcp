using System;
using System.IO;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Serialized with LeaseCliTests: both mutate the process-wide FLAUI_MCP_DATA_DIR env var, which would
// race under xUnit's default parallel-by-class execution.
[CollectionDefinition("LeaseEnv")] public class LeaseEnvCollection { }

[Collection("LeaseEnv")]
public class FileLeaseProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-lease-" + Guid.NewGuid().ToString("N"));
    public FileLeaseProviderTests() => Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", _dir);
    public void Dispose() { Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", null); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void No_file_means_no_lease()
    {
        var p = new FileLeaseProvider();
        Assert.Null(p.Read(out _));
    }

    [Fact]
    public void Reads_a_written_lease_and_its_write_time()
    {
        Directory.CreateDirectory(_dir);
        var line = InputLease.Format(DateTime.UtcNow.AddMinutes(5), "S-1-5-21-99", new[] { "shells" });
        File.WriteAllText(Path.Combine(_dir, "input.lease"), line);
        var p = new FileLeaseProvider();
        var lease = p.Read(out var writeTime);
        Assert.NotNull(lease);
        Assert.True(lease!.HasCapability("shells"));
        Assert.NotEqual(default, writeTime);
    }
}
