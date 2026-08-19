using System.Buffers.Binary;
using System.Net;
using WorkRouter.Configuration;
using WorkRouter.Core.Networking;
using WorkRouter.Monitoring;
using WorkRouter.Models;
using Xunit;

namespace WorkRouter.Tests.Monitoring;

public sealed class TrafficMonitoringTests
{
    [Fact]
    public void ParsesIpv4UdpDnsQueryAndAnswers()
    {
        var dns = new byte[] { 0, 1, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1, 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 1, 2, 3, 4 };
        var packet = Udp("192.168.137.2", "8.8.8.8", 53000, 53, dns);
        Assert.True(PacketParser.TryParseIpv4(packet, false, out var parsed));
        Assert.True(PacketParser.TryParseDns(parsed.Payload, parsed.Source, parsed.Destination, parsed.SourcePort, parsed.DestinationPort, out var query, out var answers));
        Assert.Equal("example.com", query);
        Assert.Contains(answers, answer => answer.Value == "1.2.3.4");
    }

    [Fact]
    public void RejectsMalformedAndTruncatedPacketsWithoutThrowing()
    {
        for (var length = 0; length < 96; length++)
        {
            var data = new byte[length];
            if (length > 0) data[0] = 0x45;
            _ = PacketParser.TryParseIpv4(data, false, out _);
            Assert.Null(PacketParser.TryGetHttpHost(data));
            Assert.Null(PacketParser.TryGetTlsSni(data));
        }
    }

    [Fact]
    public void ExtractsHttpHostAndDoesNotTreatOrdinaryTlsAsDoh()
    {
        Assert.Equal("example.test", PacketParser.TryGetHttpHost("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n"u8));
        var monitor = new RawSocketTrafficMonitor();
        var topology = new NetworkTopology(1, Guid.Empty, IPAddress.Parse("192.168.137.1"), NetworkAddressing.Parse("192.168.137.0/24"), "WORK", new[] { 1 });
        monitor.IngestPacket(topology, Udp("192.168.137.2", "8.8.8.8", 52000, 443, new byte[] { 0x16, 3, 3, 0, 1, 0 }));
        Assert.Equal(0, monitor.GetSummary().DoHLikeCount);
    }

    [Fact]
    public void GatewayIsNotReportedAsClient()
    {
        var monitor = new RawSocketTrafficMonitor();
        var topology = new NetworkTopology(1, Guid.Empty, IPAddress.Parse("192.168.137.1"), NetworkAddressing.Parse("192.168.137.0/24"), "WORK", new[] { 1 });
        monitor.IngestPacket(topology, Udp("192.168.137.1", "8.8.8.8", 52000, 53, new byte[] { 1, 2 }));
        Assert.DoesNotContain(monitor.GetSummary().Clients, x => x.Key == "192.168.137.1");
    }

    [Fact]
    public void AggregatesMetadataAndNeverClaimsMalware()
    {
        var monitor = new RawSocketTrafficMonitor();
        var topology = new NetworkTopology(1, Guid.Empty, IPAddress.Parse("192.168.137.1"), NetworkAddressing.Parse("192.168.137.0/24"), "WORK", new[] { 1 });
        monitor.IngestPacket(topology, Udp("192.168.137.2", "8.8.8.8", 52000, 443, new byte[] { 1, 2, 3 }));
        var summary = monitor.GetSummary();
        Assert.Contains(summary.Clients, x => x.Key == "192.168.137.2");
        Assert.Contains(summary.Destinations, x => x.Key == "8.8.8.8:443/udp");
        Assert.DoesNotContain(monitor.GetEvents(), x => x.Note?.Contains("malware confirmed", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task PreferencesAreBoundedAndRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "workrouter-pref-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new TrafficPreferencesStore(path);
            var value = await store.SaveAsync(new TrafficPreferences(true, true, 999, true));
            Assert.Equal(TrafficPreferences.MaxRetentionHours, value.RetentionHours);
            var loaded = await store.LoadAsync();
            Assert.True(loaded.AutoStartRouter); Assert.True(loaded.TrafficInspectionEnabled); Assert.True(loaded.OpenPanelAtLogin);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ParsesPktMonDnsAndTcpMetadataWithoutPayloadFile()
    {
        Assert.True(PktMonCaptureProcess.TryParsePacketLine(
            "192.168.137.2.53000 > 192.168.137.1.53: 1234+ A? analytics.example.com. (40)",
            82,
            out var dns));
        Assert.Equal("192.168.137.2", dns.Source.ToString());
        Assert.Equal("analytics.example.com", dns.Domain);
        Assert.Equal("udp", dns.Protocol);
        Assert.Equal(82, dns.Bytes);

        Assert.True(PktMonCaptureProcess.TryParsePacketLine(
            "192.168.137.2.50123 > 1.1.1.1.443: Flags [S], seq 1, win 65535, length 0",
            74,
            out var tcp));
        Assert.Equal("tcp", tcp.Protocol);
        Assert.Null(tcp.Domain);
    }

    private static byte[] Udp(string source, string destination, ushort sourcePort, ushort destinationPort, byte[] payload)
    {
        var packet = new byte[28 + payload.Length]; packet[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length); packet[8] = 64; packet[9] = 17;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12); IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), sourcePort); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), destinationPort); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(8 + payload.Length)); payload.CopyTo(packet, 28); return packet;
    }
}
