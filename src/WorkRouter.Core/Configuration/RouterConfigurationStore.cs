using System.Security.Cryptography;
using System.Text.Json;
using WorkRouter.Models;

namespace WorkRouter.Configuration;

public sealed class RouterConfigurationStore
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public RouterConfigurationStore(string? programDataRoot = null)
    {
        var root = programDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WorkRouter");
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
    }

    public async Task<RouterSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                var defaults = CreateDefaults();
                await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
                return defaults;
            }

            await using var stream = File.OpenRead(_settingsPath);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (persisted is null)
            {
                var defaults = CreateDefaults();
                await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
                return defaults;
            }

            var passphrase = string.IsNullOrEmpty(persisted.ProtectedPassphrase)
                ? GeneratePassphrase()
                : Unprotect(persisted.ProtectedPassphrase);
            return Validate(new RouterSettings(
                persisted.Ssid,
                passphrase,
                persisted.Band,
                persisted.UpstreamInterface,
                @"E:\Firmowe",
                "Firmowe"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        settings = Validate(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static RouterSettings Validate(RouterSettings settings)
    {
        var ssid = settings.Ssid.Trim();
        if (ssid.Length is < 1 or > 32 || ssid.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException("SSID musi zawierać 1–32 drukowalne znaki ASCII.", nameof(settings));
        }

        if (settings.Passphrase.Length is < 8 or > 63 || settings.Passphrase.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException("Hasło Wi-Fi musi zawierać 8–63 drukowalne znaki ASCII.", nameof(settings));
        }

        if (settings.Band is not ("Auto" or "TwoPointFourGigahertz" or "FiveGigahertz"))
        {
            throw new ArgumentException("Nieobsługiwane pasmo hotspotu.", nameof(settings));
        }

        if (!string.Equals(settings.SharePath, @"E:\Firmowe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.ShareName, "Firmowe", StringComparison.Ordinal))
        {
            throw new ArgumentException("Ścieżka i nazwa udziału są stałym kontraktem bezpieczeństwa.", nameof(settings));
        }

        return settings with { Ssid = ssid };
    }

    private static RouterSettings CreateDefaults() => new(Passphrase: GeneratePassphrase(), Band: "FiveGigahertz");

    private async Task SaveCoreAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        var persisted = new PersistedSettings(
            settings.Ssid,
            Protect(settings.Passphrase),
            settings.Band,
            settings.UpstreamInterface);
        var tempPath = _settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _settingsPath, true);
    }

    private static string GeneratePassphrase()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%+-_";
        return string.Create(24, alphabet, static (span, chars) =>
        {
            Span<byte> random = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(random);
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = chars[random[index] % chars.Length];
            }
        });
    }

    private static string Protect(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine));
    }

    private static string Unprotect(string value)
    {
        var bytes = Convert.FromBase64String(value);
        return System.Text.Encoding.UTF8.GetString(
            ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine));
    }

    private sealed record PersistedSettings(
        string Ssid,
        string ProtectedPassphrase,
        string Band,
        string UpstreamInterface);
}
