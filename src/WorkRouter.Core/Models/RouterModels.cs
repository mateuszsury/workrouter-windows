using System.Net;
using WorkRouter.Core.Networking;

namespace WorkRouter.Models;

public enum RouterOperationalState
{
    Off,
    Starting,
    On,
    Stopping,
    Faulted
}

public enum GateLevel
{
    Unknown,
    Pass,
    Warning,
    Fail
}

public sealed record GateState(string Id, string Label, GateLevel Level, string Detail);

public sealed record RouterSettings(
    string Ssid = "WORK",
    string Passphrase = "",
    string Band = "FiveGigahertz",
    string UpstreamInterface = "Ethernet",
    string SharePath = @"E:\Firmowe",
    string ShareName = "Firmowe");

public sealed record NetworkTopology(
    int InterfaceIndex,
    Guid InterfaceGuid,
    IPAddress GatewayAddress,
    IpNetwork WorkNetwork,
    string InterfaceAlias,
    IReadOnlyList<int> CandidateInterfaceIndexes);

public sealed record HotspotClientSnapshot(
    string MacAddress,
    string? HostName,
    IPAddress? Address,
    DateTimeOffset ConnectedAt);

public sealed record HotspotSnapshot(
    bool IsSupported,
    bool IsRunning,
    int ClientCount,
    int MaxClientCount,
    string Capability,
    string? Failure,
    NetworkTopology? Topology,
    IReadOnlyList<HotspotClientSnapshot> Clients,
    string? ActiveBand = null);

public sealed record IsolationHealth(
    bool Active,
    bool FiltersPresent,
    bool Ipv4Protected,
    bool Ipv6Protected,
    string Detail);

public sealed record ShareHealth(
    bool Ready,
    bool AccountReady,
    bool AclReady,
    bool EncryptionEnabled,
    bool OtherSharesDenied,
    string UncPath,
    string Detail);

public sealed record ClientUsage(
    string MacAddress,
    string? HostName,
    string? IpAddress,
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    long SessionUploadBytes,
    long SessionDownloadBytes,
    long TodayUploadBytes,
    long TodayDownloadBytes,
    bool IsPrimary,
    bool IsEstimated,
    DateTimeOffset LastSeen);

public sealed record RouterEvent(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Code,
    string Message);

public sealed record RouterStatus(
    RouterOperationalState State,
    string Summary,
    DateTimeOffset UpdatedAt,
    string? Gateway,
    string? WorkNetwork,
    RouterSettings Settings,
    IReadOnlyList<GateState> Gates,
    IReadOnlyList<ClientUsage> Clients,
    ShareHealth Share,
    bool RequiresElevation,
    bool IsDevelopmentMode,
    string? ActiveBand = null);

public sealed record OperationResult(bool Success, string Code, string Message)
{
    public static OperationResult Ok(string message) => new(true, "ok", message);
    public static OperationResult Fail(string code, string message) => new(false, code, message);
}

public sealed record ShareProvisionResult(
    OperationResult Result,
    ShareHealth Health,
    string? GeneratedPassword);
