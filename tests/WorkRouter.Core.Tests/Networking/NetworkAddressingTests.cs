using System.Net;
using WorkRouter.Core.Networking;
using Xunit;

namespace WorkRouter.Tests.Networking;

public sealed class NetworkAddressingTests
{
    [Fact]
    public void WorkSubnet_IsExcludedFromPrivateBlockList()
    {
        var work = NetworkAddressing.Parse("192.168.137.0/24");
        var blocked = NetworkAddressing.GetBlockedIpv4Ranges(work);

        Assert.DoesNotContain(blocked, n => n.Contains(IPAddress.Parse("192.168.137.1")));
        Assert.Contains(blocked, n => n.Contains(IPAddress.Parse("192.168.136.1")));
        Assert.Contains(blocked, n => n.Contains(IPAddress.Parse("192.168.138.1")));
        Assert.Contains(blocked, n => n.Contains(IPAddress.Parse("10.0.0.1")));
        Assert.Contains(blocked, n => n.Contains(IPAddress.Parse("192.168.0.1")));
        Assert.Contains(blocked, n => n.Contains(IPAddress.Parse("192.168.138.1")));
        for (var i = 0; i < blocked.Count; i++)
            for (var j = i + 1; j < blocked.Count; j++)
                Assert.False(blocked[i].Overlaps(blocked[j]));
    }

    [Fact]
    public void SubtractingMiddleRange_ProducesDisjointCidrs()
    {
        var parent = NetworkAddressing.Parse("10.0.0.0/8");
        var exclusion = NetworkAddressing.Parse("10.64.0.0/10");
        var remainder = parent.Subtract(exclusion);

        Assert.NotEmpty(remainder);
        Assert.All(remainder, n => Assert.False(n.Overlaps(exclusion)));
        Assert.DoesNotContain(remainder, n => n.Contains(IPAddress.Parse("10.64.0.1")));
        Assert.Contains(remainder, n => n.Contains(IPAddress.Parse("10.63.255.254")));
        Assert.Contains(remainder, n => n.Contains(IPAddress.Parse("10.128.0.1")));
    }

    [Fact]
    public void Ipv6Policy_IsFailClosed()
    {
        var ranges = NetworkAddressing.GetBlockedIpv6Ranges();
        Assert.Single(ranges);
        Assert.Equal(0, ranges[0].PrefixLength);
        Assert.True(ranges[0].Contains(IPAddress.Parse("2001:db8::1")));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.1.1")]
    [InlineData("172.20.1.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.10.10")]
    public void ReservedAndPrivateAddresses_AreDetected(string address)
        => Assert.True(NetworkAddressing.IsPrivateOrReserved(IPAddress.Parse(address)));
}
