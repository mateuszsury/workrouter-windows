using System.Net;

namespace WorkRouter.Models;

public sealed record TrafficPreferences(
    bool AutoStartRouter = false,
    bool TrafficInspectionEnabled = true,
    int RetentionHours = 24,
    bool OpenPanelAtLogin = false)
{
    public const int MinRetentionHours = 1;
    public const int MaxRetentionHours = 168;

    public TrafficPreferences Normalize() => this with
    {
        RetentionHours = Math.Clamp(RetentionHours, MinRetentionHours, MaxRetentionHours)
    };
}

public sealed record ParsedPacket(
    IPAddress Source,
    IPAddress Destination,
    string Protocol,
    int SourcePort,
    int DestinationPort,
    int PayloadLength,
    byte[] Payload,
    bool IsInbound);

public sealed record DnsAnswer(string Name, string Type, string Value, int TtlSeconds);

public sealed record TrafficEvent(
    long Id,
    DateTimeOffset Timestamp,
    string Client,
    string? IpAddress,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    int SourcePort,
    int DestinationPort,
    long Bytes,
    string? Domain,
    string? Host,
    string? Sni,
    string? Note,
    string VisibilitySource = "ip-only",
    double VisibilityConfidence = 0);

public sealed record TrafficAggregate(string Key, long Bytes, long Packets, DateTimeOffset LastSeen);

public sealed record TrafficAlert(
    long Id,
    DateTimeOffset Timestamp,
    string Severity,
    string Kind,
    string Client,
    string? Destination,
    string Message);

public sealed record TrafficSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    bool Enabled,
    bool Running,
    bool Volatile,
    bool PauseControlSupported,
    string? InterfaceAlias,
    IReadOnlyList<TrafficAggregate> Clients,
    IReadOnlyList<TrafficAggregate> Domains,
    IReadOnlyList<TrafficAggregate> Destinations,
    IReadOnlyList<TrafficAggregate> Protocols,
    IReadOnlyList<TrafficEvent> Timeline,
    IReadOnlyList<TrafficAlert> Alerts,
    IReadOnlyList<string> Limitations,
    long EncryptedOrUnknownCount = 0,
    long DoHLikeCount = 0,
    long DoTCount = 0,
    long QuicCount = 0,
    long VpnLikeCount = 0);

public sealed record TrafficCaptureStatus(
    bool Supported,
    bool Running,
    bool Volatile,
    int? InterfaceIndex,
    string? InterfaceAlias,
    string Detail,
    IReadOnlyList<string> Limitations);
