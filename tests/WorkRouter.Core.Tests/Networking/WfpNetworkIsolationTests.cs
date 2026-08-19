using System.Net;
using WorkRouter.Core.Networking;
using WorkRouter.Models;
using Xunit;

namespace WorkRouter.Tests.Networking;

public sealed class WfpNetworkIsolationTests
{
    [Fact]
    public async Task QuarantineRejectsMissingInterfaceWithoutOpeningPolicy()
    {
        await using var policy = new WfpNetworkIsolation();
        await Assert.ThrowsAsync<ArgumentException>(() => policy.EnterQuarantineAsync(Array.Empty<int>(), CancellationToken.None));
        var health = await policy.InspectAsync(null, CancellationToken.None);
        Assert.False(health.Active);
        Assert.False(health.FiltersPresent);
    }

    [Fact]
    public async Task ActivateRejectsIpv6TopologyFailClosed()
    {
        await using var policy = new WfpNetworkIsolation();
        var topology = new NetworkTopology(
            7,
            Guid.Empty,
            IPAddress.Parse("2001:db8::1"),
            NetworkAddressing.Parse("2001:db8::/64"),
            "WORK",
            new[] { 7 });

        await Assert.ThrowsAsync<ArgumentException>(() => policy.ActivateAsync(topology, CancellationToken.None));
        var health = await policy.InspectAsync(topology, CancellationToken.None);
        Assert.False(health.Active);
    }
}
