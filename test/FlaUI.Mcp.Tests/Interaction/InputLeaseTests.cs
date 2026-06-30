using System;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputLeaseTests
{
    [Fact]
    public void Round_trips_with_caps()
    {
        var expiry = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var line = InputLease.Format(expiry, "S-1-5-21-1", new[] { "shells" });
        Assert.True(InputLease.TryParse(line, out var lease));
        Assert.Equal(expiry, lease.ExpiryUtc);
        Assert.Equal("S-1-5-21-1", lease.Sid);
        Assert.True(lease.HasCapability("shells"));
    }

    [Fact]
    public void Empty_caps_means_no_shells()
    {
        var line = InputLease.Format(DateTime.UtcNow.AddMinutes(5), "S-1-5-21-1", System.Array.Empty<string>());
        Assert.True(InputLease.TryParse(line, out var lease));
        Assert.False(lease.HasCapability("shells"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("expiryUtc=not-a-date;sid=x;caps=")]
    public void Unparseable_is_rejected(string line) => Assert.False(InputLease.TryParse(line, out _));

    [Fact]
    public void IsValidNow_checks_expiry_and_sid()
    {
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lease = new InputLease(now.AddMinutes(5), "S-1-5-21-1", System.Array.Empty<string>());
        Assert.True(lease.IsValidNow(now, "S-1-5-21-1"));
        Assert.False(lease.IsValidNow(now.AddMinutes(6), "S-1-5-21-1"));  // expired
        Assert.False(lease.IsValidNow(now, "S-1-5-21-OTHER"));            // foreign sid
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    public void A_lease_is_invalid_when_the_current_sid_is_unresolved(string? currentSid)
    {
        var sid = "S-1-5-21-7";
        var lease = new InputLease(new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc), sid, Array.Empty<string>());
        Assert.False(lease.IsValidNow(DateTime.UtcNow, currentSid!));
    }

    [Fact]
    public void A_lease_whose_own_sid_is_unknown_never_validates()
    {
        var lease = new InputLease(new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc), "unknown", Array.Empty<string>());
        Assert.False(lease.IsValidNow(DateTime.UtcNow, "unknown"));
    }
}
