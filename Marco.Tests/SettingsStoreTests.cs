using Marco.Core.Storage;
using Xunit;

namespace Marco.Tests;

public class SettingsStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"marco-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void RoundTrips_AllOptions()
    {
        var path = TempFile();
        try
        {
            var settings = new AppSettings
            {
                IncludeBetaUpdates = true,
                TargetsText = "10.0.0.0/24\nserver01",
                Concurrency = 64,
                IcmpEnabled = false,
                TcpFallback = false,
                Classification = false,
                ResolveNames = false,
                ResolveMac = false,
                IncludeUnreachable = true,
                AutoInventory = true,
            };
            SettingsStore.Save(path, settings);
            var loaded = SettingsStore.Load(path);

            Assert.Equal(settings, loaded); // record value equality covers every property
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var loaded = SettingsStore.Load(TempFile());
        Assert.Equal(new AppSettings(), loaded);
        Assert.Null(loaded.IncludeBetaUpdates); // tri-state default: follow the build's channel
        Assert.Equal(32, loaded.Concurrency);
        Assert.True(loaded.IcmpEnabled);
    }

    [Fact]
    public void CorruptFile_YieldsDefaults_NotThrow()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ not valid json !!");
            Assert.Equal(new AppSettings(), SettingsStore.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NullBetaChoice_SurvivesRoundTrip()
    {
        var path = TempFile();
        try
        {
            SettingsStore.Save(path, new AppSettings { TargetsText = "x" });
            Assert.Null(SettingsStore.Load(path).IncludeBetaUpdates);
        }
        finally { File.Delete(path); }
    }
}
