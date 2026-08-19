using System.Text.Json;
using WorkRouter.Abstractions;
using WorkRouter.Models;

namespace WorkRouter.Service;

internal sealed class StartupShortcutManager : IStartupShortcutManager
{
    private readonly string _installationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkRouter", "installation.json");

    public async Task<OperationResult> SetOpenPanelAtLoginAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_installationPath)) return OperationResult.Fail("startup_unavailable", "Brak manifestu instalacji launchera.");
            await using var stream = new FileStream(_installationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var manifest = await JsonSerializer.DeserializeAsync<InstallationManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.MenuShortcutPath) || string.IsNullOrWhiteSpace(manifest.StartupShortcutPath))
                return OperationResult.Fail("startup_unavailable", "Manifest instalacji nie zawiera ścieżek skrótów.");
            var source = Path.GetFullPath(manifest.MenuShortcutPath);
            var target = Path.GetFullPath(manifest.StartupShortcutPath);
            if (!string.Equals(Path.GetExtension(source), ".lnk", StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(target), ".lnk", StringComparison.OrdinalIgnoreCase))
                return OperationResult.Fail("startup_unavailable", "Nieprawidłowy typ skrótu.");
            if (!enabled)
            {
                if (File.Exists(target)) File.Delete(target);
                return OperationResult.Ok("Autostart panelu wyłączony.");
            }
            if (!File.Exists(source)) return OperationResult.Fail("startup_unavailable", "Brak skrótu panelu w menu Start.");
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(directory)) return OperationResult.Fail("startup_unavailable", "Nieprawidłowy katalog Startup.");
            Directory.CreateDirectory(directory);
            File.Copy(source, target, true);
            return OperationResult.Ok("Autostart panelu włączony.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return OperationResult.Fail("startup_failed", ex.Message); }
    }

    public async Task<bool> IsOpenPanelAtLoginEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_installationPath)) return false;
            await using var stream = new FileStream(_installationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var manifest = await JsonSerializer.DeserializeAsync<InstallationManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return manifest?.StartupShortcutPath is { Length: > 0 } path && File.Exists(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    private sealed record InstallationManifest(string? LauncherPath, string? MenuShortcutPath, string? StartupShortcutPath, string? InstalledForSid);
}
