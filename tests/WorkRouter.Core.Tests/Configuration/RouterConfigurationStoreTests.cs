using WorkRouter.Configuration;
using WorkRouter.Models;

namespace WorkRouter.Tests.Configuration;

public sealed class RouterConfigurationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkRouterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsProtectedSettings()
    {
        var store = new RouterConfigurationStore(_root);
        var settings = new RouterSettings("WORK-TEST", "ThisIsAStrongPassphrase!42", "FiveGigahertz");

        await store.SaveAsync(settings, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(settings, loaded);
        var persisted = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        Assert.DoesNotContain(settings.Passphrase, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstLoad_PersistsGeneratedPassphrase()
    {
        var store = new RouterConfigurationStore(_root);

        var first = await store.LoadAsync(CancellationToken.None);
        var second = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(first.Passphrase, second.Passphrase);
        Assert.InRange(first.Passphrase.Length, 12, 63);
        var persisted = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        Assert.DoesNotContain(first.Passphrase, persisted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("hasło-z-polskim-znakiem")]
    public void Validate_RejectsInvalidPassphrase(string passphrase)
    {
        var settings = new RouterSettings(Passphrase: passphrase);

        Assert.Throws<ArgumentException>(() => RouterConfigurationStore.Validate(settings));
    }

    [Fact]
    public void Validate_AcceptsStandardEightToSixtyThreeCharacterPassphrase()
    {
        const string examplePassphrase = "ExamplePass123";
        var settings = new RouterSettings(Passphrase: examplePassphrase);

        Assert.Equal(examplePassphrase, RouterConfigurationStore.Validate(settings).Passphrase);
    }

    [Fact]
    public void RouterDefaultsPreferFiveGigahertz()
    {
        Assert.Equal("FiveGigahertz", new RouterSettings().Band);
    }

    [Fact]
    public void Validate_RejectsChangingShareBoundary()
    {
        var settings = new RouterSettings(Passphrase: "ValidPassword!123", SharePath: @"E:\Other");

        var error = Assert.Throws<ArgumentException>(() => RouterConfigurationStore.Validate(settings));
        Assert.Contains("stałym kontraktem", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
