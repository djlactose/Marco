using System.Text.Json;
using Marco.Core.Clients;
using Marco.Core.Inventory;
using Marco.Credentials;
using Xunit;

namespace Marco.Tests;

public class ClientProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "marco-clients-" + Guid.NewGuid().ToString("N")[..8]);

    public ClientProfileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Store_UpsertsAndDeletes_SortedByName()
    {
        var store = new ClientProfileStore(Path.Combine(_dir, "clients.json"));
        var beta = ClientProfile.New("Beta Corp", "10.2.0.0/24");
        var acme = ClientProfile.New("Acme", "10.1.0.0/24");
        store.Upsert(beta);
        store.Upsert(acme);

        var loaded = store.Load();
        Assert.Equal(new[] { "Acme", "Beta Corp" }, loaded.Select(p => p.Name)); // name order

        store.Upsert(acme with { TargetsText = "10.9.0.0/24" });
        Assert.Equal("10.9.0.0/24", store.Load().Single(p => p.Id == acme.Id).TargetsText);

        store.Delete(beta.Id);
        Assert.Single(store.Load());
    }

    [Fact]
    public void Store_RoundTripsPerClientScanConfig()
    {
        var store = new ClientProfileStore(Path.Combine(_dir, "clients.json"));
        var acme = ClientProfile.New("Acme", "10.1.0.0/24") with
        {
            Concurrency = 64, IcmpEnabled = false, TcpFallback = true, Classification = false,
            ResolveNames = false, ResolveMac = true, IncludeUnreachable = true, AutoInventory = true,
            GroupByBlock = false, CollectorOverrides = new Dictionary<string, bool> { ["UsbHistory"] = true },
        };
        store.Upsert(acme);

        var loaded = store.Load().Single();
        Assert.Equal(64, loaded.Concurrency);
        Assert.False(loaded.IcmpEnabled);
        Assert.False(loaded.Classification);
        Assert.True(loaded.IncludeUnreachable);
        Assert.True(loaded.AutoInventory);
        Assert.False(loaded.GroupByBlock);
        Assert.True(loaded.CollectorOverrides!["UsbHistory"]);
    }

    [Fact]
    public void OldClientsFile_WithoutScanConfig_LoadsWithDefaults()
    {
        // A clients.json written before per-client scan config existed.
        var path = Path.Combine(_dir, "clients.json");
        File.WriteAllText(path, """
            { "SchemaVersion": 1, "Profiles": [
              { "Id": "acme", "Name": "Acme", "TargetsText": "10.1.0.0/24", "CreatedUtc": "2026-01-01T00:00:00Z" } ] }
            """);
        var loaded = new ClientProfileStore(path).Load().Single();

        Assert.Equal(32, loaded.Concurrency);       // constructor defaults fill the missing fields
        Assert.True(loaded.IcmpEnabled);
        Assert.True(loaded.GroupByBlock);
        Assert.Null(loaded.CollectorOverrides);
    }

    [Fact]
    public void SharedFile_CarriesScanConfig_ButStillNoSecrets()
    {
        var profile = ClientProfile.New("Acme", "10.1.0.0/24") with { Concurrency = 48, Classification = false };
        var exported = Path.Combine(_dir, "acme" + ClientProfileSharing.Extension);
        ClientProfileSharing.Export(profile, exported);

        var json = File.ReadAllText(exported);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);

        var imported = ClientProfileSharing.Import(exported, Path.Combine(_dir, "logos"));
        Assert.Equal(48, imported.Concurrency);      // scan recipe travels with the shared profile
        Assert.False(imported.Classification);
    }

    [Fact]
    public void Store_ConcurrentUpserts_BothSurvive()
    {
        var path = Path.Combine(_dir, "clients.json");
        var a = new ClientProfileStore(path);
        var b = new ClientProfileStore(path);
        Parallel.Invoke(
            () => a.Upsert(ClientProfile.New("From-A")),
            () => b.Upsert(ClientProfile.New("From-B")));
        Assert.Equal(2, a.Load().Count);
    }

    [Fact]
    public void SharedFile_CarriesNoCredentialFields_AndEmbedsLogo()
    {
        var logoSource = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(logoSource, new byte[] { 1, 2, 3, 4 });
        var profile = ClientProfile.New("Acme", "10.1.0.0/24") with
        {
            CompanyName = "Acme Inc", LogoPath = logoSource, AccentColor = "#123456",
        };

        var exported = Path.Combine(_dir, "acme" + ClientProfileSharing.Extension);
        ClientProfileSharing.Export(profile, exported);

        var json = File.ReadAllText(exported);
        // The schema simply has no place for secrets — assert nothing credential-shaped leaked.
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(logoSource.Replace("\\", "\\\\"), json); // local path blanked
        Assert.Contains("LogoBase64", json);

        var logosDir = Path.Combine(_dir, "logos");
        var imported = ClientProfileSharing.Import(exported, logosDir);
        Assert.Equal(profile.Id, imported.Id);                          // stable identity across shares
        Assert.Equal("Acme Inc", imported.CompanyName);
        Assert.True(File.Exists(imported.LogoPath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(imported.LogoPath!));
    }

    [Fact]
    public void CredentialScoping_ClientFirst_ThenShared_OtherClientsExcluded()
    {
        using var store = new CredentialStore();
        store.Add(new CredentialSet("shared-admin", "CORP", "admin", null));
        store.Add(new CredentialSet("acme-admin", null, "acmeadmin", null) { ClientId = "acme" });
        store.Add(new CredentialSet("beta-admin", null, "betaadmin", null) { ClientId = "beta" });

        var forAcme = store.ToCandidatesFor("acme");
        Assert.Equal(new[] { "acme-admin", "shared-admin" }, forAcme.Select(c => c.Label)); // client first, no beta

        var noClient = store.ToCandidatesFor(null);
        Assert.Equal(new[] { "shared-admin" }, noClient.Select(c => c.Label)); // scoped sets stay home
    }

    [Fact]
    public void CredentialClientId_SurvivesDpapiRoundTrip()
    {
        var path = Path.Combine(_dir, "credentials.dat");
        using (var store = new CredentialStore())
        {
            var set = new CredentialSet("acme-admin", null, "admin", null) { ClientId = "acme-id" };
            set.SetPassword("hunter2");
            store.Add(set);
            store.Save(path);
        }
        using var reloaded = new CredentialStore();
        reloaded.Load(path);
        Assert.Equal("acme-id", reloaded.Sets.Single().ClientId);
    }

    [Fact]
    public void OldCredentialFile_WithoutClientId_LoadsAsShared()
    {
        // Simulate a pre-clients credentials.dat: serialize entries lacking the ClientId property.
        var path = Path.Combine(_dir, "old-credentials.dat");
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            new { Label = "legacy", Domain = "CORP", Username = "admin", IsCurrentToken = false,
                  ProtectedPassword = (string?)null, Kind = CredentialKind.Windows, SshPort = 22 },
        }));
        using var store = new CredentialStore();
        store.Load(path);
        Assert.Null(store.Sets.Single().ClientId);
    }
}
