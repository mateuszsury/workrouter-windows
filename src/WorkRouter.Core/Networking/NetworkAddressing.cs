using System.Net;
using System.Numerics;

namespace WorkRouter.Core.Networking;

/// <summary>An IPv4 or IPv6 CIDR network. All arithmetic is performed on the masked network address.</summary>
public readonly record struct IpNetwork(IPAddress NetworkAddress, int PrefixLength)
{
    public bool IsIPv4 => NetworkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    public int BitLength => IsIPv4 ? 32 : 128;

    public static IpNetwork FromAddress(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength is < 0 || prefixLength > bits)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));

        var value = ToInteger(address);
        var mask = prefixLength == 0 ? BigInteger.Zero : ((BigInteger.One << prefixLength) - 1) << (bits - prefixLength);
        return new IpNetwork(ToAddress(value & mask, address.AddressFamily), prefixLength);
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != NetworkAddress.AddressFamily)
            return false;
        var candidate = ToInteger(address);
        var network = ToInteger(NetworkAddress);
        var mask = PrefixLength == 0 ? BigInteger.Zero : ((BigInteger.One << PrefixLength) - 1) << (BitLength - PrefixLength);
        return (candidate & mask) == network;
    }

    public bool Overlaps(IpNetwork other)
        => NetworkAddress.AddressFamily == other.NetworkAddress.AddressFamily
           && (Contains(other.NetworkAddress) || other.Contains(NetworkAddress));

    /// <summary>Subtracts an overlapping network and returns the minimum CIDR cover of the remainder.</summary>
    public IReadOnlyList<IpNetwork> Subtract(IpNetwork exclusion)
    {
        if (NetworkAddress.AddressFamily != exclusion.NetworkAddress.AddressFamily || !Overlaps(exclusion))
            return new[] { this };

        var bits = BitLength;
        var start = ToInteger(NetworkAddress);
        var end = start + ((BigInteger.One << (bits - PrefixLength)) - 1);
        var excludedStart = BigInteger.Max(start, ToInteger(exclusion.NetworkAddress));
        var excludedEnd = BigInteger.Min(end, ToInteger(exclusion.NetworkAddress) + ((BigInteger.One << (bits - exclusion.PrefixLength)) - 1));
        var result = new List<IpNetwork>();
        AddRangeAsCidrs(start, excludedStart - 1, bits, NetworkAddress.AddressFamily, result);
        AddRangeAsCidrs(excludedEnd + 1, end, bits, NetworkAddress.AddressFamily, result);
        return result;
    }

    public override string ToString() => $"{NetworkAddress}/{PrefixLength}";

    private static void AddRangeAsCidrs(BigInteger start, BigInteger end, int bits, System.Net.Sockets.AddressFamily family, ICollection<IpNetwork> result)
    {
        if (start > end)
            return;
        while (start <= end)
        {
            // A block can start at the largest power-of-two boundary represented
            // by the trailing zero count of its address. The previous complement
            // calculation admitted misaligned CIDRs for addresses such as x.x.1.0.
            var maxAligned = start.IsZero ? bits : GetLowestSetBit(start, bits);
            var remaining = end - start + BigInteger.One;
            var maxCountBits = FloorLog2(remaining);
            var blockBits = Math.Min(maxAligned, maxCountBits);
            var prefix = bits - blockBits;
            result.Add(new IpNetwork(ToAddress(start, family), prefix));
            start += BigInteger.One << blockBits;
        }
    }

    private static int GetLowestSetBit(BigInteger value, int bits)
    {
        for (var i = 0; i < bits; i++)
            if (((value >> i) & BigInteger.One) != BigInteger.Zero)
                return i;
        return bits;
    }

    private static int FloorLog2(BigInteger value)
    {
        var result = -1;
        while (value > BigInteger.Zero)
        {
            value >>= 1;
            result++;
        }
        return result;
    }

    internal static BigInteger ToInteger(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var value = BigInteger.Zero;
        foreach (var b in bytes)
            value = (value << 8) | b;
        return value;
    }

    internal static IPAddress ToAddress(BigInteger value, System.Net.Sockets.AddressFamily family)
    {
        var length = family == System.Net.Sockets.AddressFamily.InterNetwork ? 4 : 16;
        var bytes = new byte[length];
        for (var i = length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xff);
            value >>= 8;
        }
        return new IPAddress(bytes);
    }
}

public static class NetworkAddressing
{
    private static readonly IpNetwork[] PrivateIpv4 =
    {
        Parse("0.0.0.0/8"),       // unspecified/reserved
        Parse("10.0.0.0/8"),
        Parse("100.64.0.0/10"),   // shared/CGNAT
        Parse("127.0.0.0/8"),
        Parse("169.254.0.0/16"), // link-local
        Parse("172.16.0.0/12"),
        Parse("192.0.0.0/24"),
        Parse("192.0.2.0/24"),   // documentation/reserved
        Parse("192.168.0.0/16"),
        Parse("198.18.0.0/15"),  // benchmark networks
        Parse("198.51.100.0/24"),
        Parse("203.0.113.0/24"),
        Parse("224.0.0.0/4"),    // multicast
        Parse("240.0.0.0/4"),    // reserved
    };

    public static IpNetwork Parse(string cidr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
            throw new FormatException($"Invalid CIDR network '{cidr}'.");
        return IpNetwork.FromAddress(address, prefix);
    }

    public static bool IsPrivateOrReserved(IPAddress address)
        => PrivateIpv4.Any(n => n.Contains(address));

    /// <summary>Returns reserved/private ranges, excluding the current WORK subnet.</summary>
    public static IReadOnlyList<IpNetwork> GetBlockedIpv4Ranges(IpNetwork workSubnet)
    {
        if (!workSubnet.IsIPv4)
            throw new ArgumentException("WORK subnet must be IPv4.", nameof(workSubnet));
        var result = new List<IpNetwork>();
        foreach (var network in PrivateIpv4)
            result.AddRange(network.Subtract(workSubnet));
        return result;
    }

    public static IReadOnlyList<IpNetwork> GetBlockedIpv6Ranges()
        => new[]
        {
            Parse("::/0"), // v1 policy deliberately fail-closed for forwarded IPv6
        };
}
