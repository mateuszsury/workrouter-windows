using WorkRouter.Models;

namespace WorkRouter.Service;

internal sealed record SettingsRequest(string Ssid, string Band, int MaxClients, string? Password);
internal sealed record PrimaryClientRequest(string MacAddress);
internal sealed record PreferencesRequest(bool? AutoStartRouter, bool? TrafficInspectionEnabled, int? RetentionHours, bool? OpenPanelAtLogin);

internal static class ApiModels
{
    public static object Status(RouterStatus status)
    {
        var ethernet = status.Gates.FirstOrDefault(gate => gate.Id == "ethernet");
        var ipv4 = status.Gates.FirstOrDefault(gate => gate.Id == "wfp-ipv4");
        var ipv6 = status.Gates.FirstOrDefault(gate => gate.Id == "wfp-ipv6");
        var downloadRate = status.Clients.Sum(client => client.DownloadBytesPerSecond);
        var uploadRate = status.Clients.Sum(client => client.UploadBytesPerSecond);
        var totalBytes = status.Clients.Sum(client => client.SessionDownloadBytes + client.SessionUploadBytes);
        return new
        {
            routerRunning = status.State == RouterOperationalState.On,
            state = status.State.ToString(),
            status.Summary,
            status.UpdatedAt,
            status.Gateway,
            workNetwork = status.WorkNetwork,
            ssid = status.Settings.Ssid,
            wifiPassword = status.Settings.Passphrase,
            band = ToUiBand(status.Settings.Band),
            activeBand = ToUiBand(status.ActiveBand),
            bandConfirmed = status.State == RouterOperationalState.On
                && status.ActiveBand is not null
                && (status.Settings.Band == "Auto"
                    || string.Equals(status.Settings.Band, status.ActiveBand, StringComparison.OrdinalIgnoreCase)),
            maxClients = 8,
            ethernetOnline = ethernet?.Level == GateLevel.Pass,
            ipv4Filtered = ipv4?.Level == GateLevel.Pass,
            ipv6Blocked = ipv6?.Level == GateLevel.Pass,
            smbReady = status.Share.Ready,
            sharePath = status.Share.UncPath,
            shareAccount = $"{Environment.MachineName}\\workshare",
            downloadRate,
            uploadRate,
            totalBytes,
            trafficEstimated = status.Clients.Any(client => client.IsEstimated && client.IsPrimary),
            status.RequiresElevation,
            gates = status.Gates.Select(gate => new
            {
                gate.Id,
                gate.Label,
                level = gate.Level.ToString(),
                gate.Detail
            })
        };
    }

    public static object Clients(IReadOnlyList<ClientUsage> clients) => new
    {
        clients = clients.Select(client => new
        {
            id = client.MacAddress,
            mac = client.MacAddress,
            name = client.HostName ?? "Nieznane urządzenie",
            ip = client.IpAddress,
            connectedAt = client.LastSeen,
            downloadBytes = client.SessionDownloadBytes,
            uploadBytes = client.SessionUploadBytes,
            downloadRate = client.DownloadBytesPerSecond,
            uploadRate = client.UploadBytesPerSecond,
            client.IsPrimary,
            client.IsEstimated
        })
    };

    public static string FromUiBand(string band) => band switch
    {
        "2.4GHz" => "TwoPointFourGigahertz",
        "5GHz" => "FiveGigahertz",
        _ => "Auto"
    };

    private static string? ToUiBand(string? band) => band switch
    {
        "TwoPointFourGigahertz" => "2.4GHz",
        "FiveGigahertz" => "5GHz",
        "Auto" => "auto",
        _ => null
    };
}
