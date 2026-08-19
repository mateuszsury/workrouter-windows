using System.Buffers.Binary;
using System.Net;
using System.Text;
using WorkRouter.Models;

namespace WorkRouter.Monitoring;

/// <summary>Pure, bounded parsers for metadata only. Payload bytes are never retained by the monitor.</summary>
public static class PacketParser
{
    public static bool TryParseIpv4(ReadOnlySpan<byte> packet, bool inbound, out ParsedPacket result)
    {
        result = default!;
        if (packet.Length < 20 || (packet[0] >> 4) != 4) return false;
        var ihl = (packet[0] & 0x0f) * 4;
        if (ihl < 20 || ihl > packet.Length) return false;
        var total = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if (total < ihl || total > packet.Length) total = (ushort)packet.Length;
        var protocol = packet[9] switch { 6 => "tcp", 17 => "udp", _ => "other" };
        var source = new IPAddress(packet[12..16]);
        var destination = new IPAddress(packet[16..20]);
        var sourcePort = 0; var destinationPort = 0; var payloadOffset = ihl;
        if ((protocol == "tcp" || protocol == "udp") && total >= ihl + 4)
        {
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet[ihl..(ihl + 2)]);
            destinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet[(ihl + 2)..(ihl + 4)]);
            if (protocol == "tcp")
            {
                if (total < ihl + 20) return false;
                var headerLength = (packet[ihl + 12] >> 4) * 4;
                if (headerLength < 20 || ihl + headerLength > total) return false;
                payloadOffset = ihl + headerLength;
            }
            else
            {
                if (total < ihl + 8) return false;
                payloadOffset = ihl + 8;
            }
        }
        var length = Math.Max(0, total - payloadOffset);
        length = Math.Min(length, 64 * 1024);
        result = new ParsedPacket(source, destination, protocol, sourcePort, destinationPort,
            length, packet.Slice(payloadOffset, length).ToArray(), inbound);
        return true;
    }

    public static bool TryParseDns(ReadOnlySpan<byte> payload, IPAddress source, IPAddress destination,
        int sourcePort, int destinationPort, out string? query, out IReadOnlyList<DnsAnswer> answers)
    {
        query = null; answers = Array.Empty<DnsAnswer>();
        if (sourcePort != 53 && destinationPort != 53 || payload.Length < 12) return false;
        // TCP DNS prepends a two-byte message length; UDP starts directly with ID.
        if (payload.Length >= 14 && BinaryPrimitives.ReadUInt16BigEndian(payload[..2]) == payload.Length - 2)
            payload = payload[2..];
        var qd = BinaryPrimitives.ReadUInt16BigEndian(payload[4..6]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(payload[6..8]);
        var offset = 12;
        if (qd > 0 && !TryReadName(payload, ref offset, out query)) return false;
        if (offset + 4 > payload.Length) return false;
        offset += 4;
        var list = new List<DnsAnswer>();
        for (var i = 0; i < an && i < 128; i++)
        {
            if (!TryReadName(payload, ref offset, out var name) || offset + 10 > payload.Length) break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            var ttl = BinaryPrimitives.ReadInt32BigEndian(payload[(offset + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload[(offset + 8)..]);
            offset += 10;
            if (offset + length > payload.Length) break;
            var value = type switch
            {
                1 when length == 4 => new IPAddress(payload.Slice(offset, 4)).ToString(),
                28 when length == 16 => new IPAddress(payload.Slice(offset, 16)).ToString(),
                _ => null
            };
            if (value is not null) list.Add(new DnsAnswer(name!, type == 1 ? "A" : "AAAA", value, ttl));
            offset += length;
        }
        answers = list;
        return query is not null || list.Count > 0;
    }

    private static bool TryReadName(ReadOnlySpan<byte> data, ref int offset, out string? name)
    {
        name = null; var labels = new List<string>(); var cursor = offset; var jumped = false;
        for (var i = 0; i < 128; i++)
        {
            if (cursor >= data.Length) return false;
            var len = data[cursor++];
            if (len == 0) { if (!jumped) offset = cursor; name = string.Join('.', labels); return true; }
            if ((len & 0xc0) == 0xc0)
            {
                if (cursor >= data.Length) return false;
                var pointer = ((len & 0x3f) << 8) | data[cursor++];
                if (pointer >= data.Length || pointer == offset) return false;
                if (!jumped) { offset = cursor; jumped = true; }
                cursor = pointer; continue;
            }
            if (len > 63 || cursor + len > data.Length) return false;
            labels.Add(Encoding.ASCII.GetString(data.Slice(cursor, len)));
            cursor += len;
        }
        return false;
    }

    public static string? TryGetHttpHost(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > 32 * 1024) return null;
        var text = Encoding.ASCII.GetString(payload);
        var firstLine = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLine < 0) return null;
        var method = text[..firstLine];
        if (!(method.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) || method.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) || method.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase) || method.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))) return null;
        foreach (var line in text[(firstLine + 2)..].Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) return line[5..].Trim().Split(':')[0];
        return null;
    }

    public static string? TryGetTlsSni(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 22 || payload[1] != 3) return null;
        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(payload[3..5]);
        if (recordLength > payload.Length - 5 || recordLength < 4) return null;
        var p = 5; if (p + 4 > payload.Length || payload[p] != 1) return null;
        var helloLength = (payload[p + 1] << 16) | (payload[p + 2] << 8) | payload[p + 3]; p += 4;
        var end = Math.Min(payload.Length, p + helloLength);
        if (p + 2 + 32 + 1 > end) return null;
        p += 2 + 32; var sessionLength = payload[p++]; if (p + sessionLength + 2 > end) return null; p += sessionLength;
        var cipherLength = BinaryPrimitives.ReadUInt16BigEndian(payload[p..]); p += 2; if (p + cipherLength + 1 > end) return null; p += cipherLength;
        var compressionLength = payload[p++]; if (p + compressionLength + 2 > end) return null; p += compressionLength;
        var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(payload[p..]); p += 2; var extEnd = Math.Min(end, p + extensionsLength);
        while (p + 4 <= extEnd)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(payload[p..]); var size = BinaryPrimitives.ReadUInt16BigEndian(payload[(p + 2)..]); p += 4;
            if (p + size > extEnd) return null;
            if (type == 0 && size >= 5)
            {
                var listLength = BinaryPrimitives.ReadUInt16BigEndian(payload[p..]); var q = p + 2; var listEnd = Math.Min(p + size, q + listLength);
                while (q + 3 <= listEnd) { var kind = payload[q++]; var n = BinaryPrimitives.ReadUInt16BigEndian(payload[q..]); q += 2; if (q + n > listEnd) return null; if (kind == 0) return Encoding.ASCII.GetString(payload.Slice(q, n)); q += n; }
            }
            p += size;
        }
        return null;
    }
}
