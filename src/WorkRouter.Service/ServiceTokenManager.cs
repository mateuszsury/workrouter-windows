using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;

namespace WorkRouter.Service;

internal sealed class ServiceTokenManager
{
    public const string CookieName = "workrouter-session";
    private readonly byte[] _tokenBytes;
    private readonly string _token;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _bootstrapTickets = new(StringComparer.Ordinal);

    public ServiceTokenManager(IConfiguration configuration)
    {
        (_tokenBytes, _token) = LoadOrCreateToken();
        Url = configuration["WorkRouter:Url"] ?? "http://127.0.0.1:17437";
    }

    public string Url { get; }
    public string Token => _token;

    public string CreateBootstrapTicket()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in _bootstrapTickets.Where(entry => entry.Value <= now))
        {
            _bootstrapTickets.TryRemove(stale.Key, out _);
        }

        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _bootstrapTickets[ticket] = now.AddSeconds(30);
        return ticket;
    }

    public bool TryConsumeBootstrapTicket(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || !_bootstrapTickets.TryRemove(ticket, out var expiresAt))
        {
            return false;
        }
        return expiresAt > DateTimeOffset.UtcNow;
    }

    public bool IsValid(string? candidate)
    {
        if (candidate is null || candidate.Length != _token.Length)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(candidate);
            return CryptographicOperations.FixedTimeEquals(bytes, _tokenBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task WriteEndpointFileAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WorkRouter");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "endpoint.json");
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, new { Url, Token }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, true);
    }

    private static (byte[] Bytes, string Text) LoadOrCreateToken()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WorkRouter",
                "endpoint.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("Token", out var property) ||
                    document.RootElement.TryGetProperty("token", out property))
                {
                    var existing = property.GetString();
                    if (existing is { Length: 64 })
                    {
                        var bytes = Convert.FromHexString(existing);
                        if (bytes.Length == 32)
                        {
                            return (bytes, existing);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // A missing or damaged endpoint file is recovered by rotating the
            // local token. The installer ACL still controls who can read it.
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        return (generated, Convert.ToHexString(generated));
    }
}
