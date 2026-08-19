using System.Text.Json;
using WorkRouter.Models;

namespace WorkRouter.Configuration;

public sealed class TrafficPreferencesStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TrafficPreferencesStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkRouter", "preferences.json")) { }

    public TrafficPreferencesStore(string path) => _path = path;

    public async Task<TrafficPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new TrafficPreferences();
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var value = await JsonSerializer.DeserializeAsync<TrafficPreferences>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return (value ?? new TrafficPreferences()).Normalize();
        }
        catch (JsonException) { return new TrafficPreferences(); }
        finally { _gate.Release(); }
    }

    public async Task<TrafficPreferences> SaveAsync(TrafficPreferences value, CancellationToken cancellationToken = default)
    {
        var normalized = value.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, true);
            return normalized;
        }
        finally { _gate.Release(); }
    }
}
