using Marco.Credentials;
using Xunit;

namespace Marco.Tests;

public class CredentialStoreTests
{
    [Fact]
    public void ToCandidates_PreservesOrderAndLabels()
    {
        using var store = new CredentialStore();
        var a = new CredentialSet("A", "CORP", "admin", null);
        a.SetPassword("pw1");
        store.Add(a);
        store.Add(CredentialSet.CurrentToken("Session"));

        var candidates = store.ToCandidates();
        Assert.Equal(2, candidates.Count);
        Assert.Equal("A", candidates[0].Label);
        Assert.Equal("admin", candidates[0].Credential.Username);
        Assert.True(candidates[1].Credential.UseCurrentToken);
    }

    [Fact]
    public void CurrentToken_ProducesNoCredentialMaterial()
    {
        var set = CredentialSet.CurrentToken();
        var wmi = set.ToWmiCredential();
        Assert.True(wmi.UseCurrentToken);
        Assert.Null(wmi.Username);
        Assert.Null(wmi.Password);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsUnderCurrentUser()
    {
        var path = Path.Combine(Path.GetTempPath(), "marco-cred-" + Guid.NewGuid().ToString("N")[..8] + ".dat");
        try
        {
            using (var store = new CredentialStore())
            {
                var a = new CredentialSet("Domain admin", "CORP", "admin", null);
                a.SetPassword("s3cret!");
                store.Add(a);
                store.Add(CredentialSet.CurrentToken("Session"));
                store.Save(path);

                // The persisted file must never contain the plaintext password.
                var raw = File.ReadAllText(path);
                Assert.DoesNotContain("s3cret!", raw);
            }

            using (var loaded = new CredentialStore())
            {
                loaded.Load(path);
                var sets = loaded.Sets;
                Assert.Equal(2, sets.Count);
                Assert.Equal("Domain admin", sets[0].Label);
                Assert.Equal("admin", sets[0].Username);
                Assert.NotNull(sets[0].Password);
                Assert.True(sets[1].IsCurrentToken);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CredentialKind_FiltersByHostType()
    {
        var win = new Marco.Core.Inventory.CredentialCandidate("w", new Marco.Core.Wmi.WmiCredential { Username = "a" }, Marco.Core.Inventory.CredentialKind.Windows);
        var lin = new Marco.Core.Inventory.CredentialCandidate("l", new Marco.Core.Wmi.WmiCredential { Username = "b" }, Marco.Core.Inventory.CredentialKind.Linux, SshPort: 2222);
        var any = new Marco.Core.Inventory.CredentialCandidate("x", new Marco.Core.Wmi.WmiCredential { Username = "c" });

        Assert.True(win.AppliesTo(Marco.Core.Inventory.CredentialKind.Windows));
        Assert.False(win.AppliesTo(Marco.Core.Inventory.CredentialKind.Linux));
        Assert.True(lin.AppliesTo(Marco.Core.Inventory.CredentialKind.Linux));
        Assert.False(lin.AppliesTo(Marco.Core.Inventory.CredentialKind.Windows));
        Assert.True(any.AppliesTo(Marco.Core.Inventory.CredentialKind.Windows));
        Assert.True(any.AppliesTo(Marco.Core.Inventory.CredentialKind.Linux));
        Assert.Equal(2222, lin.SshPort);
    }

    [Fact]
    public void SaveAndLoad_PreservesKindAndPort()
    {
        var path = Path.Combine(Path.GetTempPath(), "marco-cred-" + Guid.NewGuid().ToString("N")[..8] + ".dat");
        try
        {
            using (var store = new CredentialStore())
            {
                var lin = new CredentialSet("web-ssh", null, "root", null)
                    { Kind = Marco.Core.Inventory.CredentialKind.Linux, SshPort = 2222 };
                lin.SetPassword("pw");
                store.Add(lin);
                store.Save(path);
            }
            using (var loaded = new CredentialStore())
            {
                loaded.Load(path);
                var s = loaded.Sets[0];
                Assert.Equal(Marco.Core.Inventory.CredentialKind.Linux, s.Kind);
                Assert.Equal(2222, s.SshPort);
                Assert.Equal("root", s.Username);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CurrentToken_CandidateIsWindowsKind()
    {
        // The no-credentials fallback must never be tried against SSH hosts (a session token can't be used there).
        var candidate = CredentialSet.CurrentToken().ToCandidate();
        Assert.Equal(Marco.Core.Inventory.CredentialKind.Windows, candidate.Kind);
        Assert.False(candidate.AppliesTo(Marco.Core.Inventory.CredentialKind.Linux));
    }

    [Fact]
    public void ReloadIfChanged_PicksUpAnotherInstancesSave_ThenIsIdempotent()
    {
        // Two stores on one file = two Marco windows sharing credentials.dat.
        var path = Path.Combine(Path.GetTempPath(), "marco-cred-" + Guid.NewGuid().ToString("N")[..8] + ".dat");
        try
        {
            using var windowA = new CredentialStore();
            using var windowB = new CredentialStore();
            windowA.Add(new CredentialSet("A", "CORP", "a", null));
            windowA.Save(path);
            windowB.Load(path);

            Assert.False(windowB.ReloadIfChanged(path)); // nothing changed since B loaded

            windowA.Add(new CredentialSet("A2", "CORP", "a2", null));
            windowA.Save(path);

            Assert.True(windowB.ReloadIfChanged(path));   // A's save is picked up...
            Assert.Equal(new[] { "A", "A2" }, windowB.Sets.Select(s => s.Label));
            Assert.False(windowB.ReloadIfChanged(path));  // ...exactly once

            // B's own save then counts as "seen" too.
            windowB.Add(new CredentialSet("B", "CORP", "b", null));
            windowB.Save(path);
            Assert.False(windowB.ReloadIfChanged(path));
            Assert.True(windowA.ReloadIfChanged(path));
            Assert.Equal(3, windowA.Sets.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "marco-cred-" + Guid.NewGuid().ToString("N")[..8] + ".dat");
        try
        {
            using var store = new CredentialStore();
            store.Add(CredentialSet.CurrentToken("Session"));
            store.Save(path);
            store.Save(path); // overwrite path must work too
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Replace_KeepsTryOrderPosition()
    {
        using var store = new CredentialStore();
        var a = new CredentialSet("A", "CORP", "a", null);
        var b = new CredentialSet("B", "CORP", "b", null);
        var c = new CredentialSet("C", "CORP", "c", null);
        store.Add(a); store.Add(b); store.Add(c);

        var edited = new CredentialSet("B2", "CORP", "b2", null);
        store.Replace(b, edited);

        var labels = store.ToCandidates().Select(x => x.Label).ToList();
        Assert.Equal(new[] { "A", "B2", "C" }, labels);
    }

    [Fact]
    public void IsLocalAccount_DetectsUacFilteringCase()
    {
        var local = new Marco.Core.Wmi.WmiCredential { Username = "administrator" }; // no domain
        Assert.True(local.IsLocalAccount("SERVER01"));

        var domain = new Marco.Core.Wmi.WmiCredential { Domain = "CORP", Username = "admin" };
        Assert.False(domain.IsLocalAccount("SERVER01"));

        var dotLocal = new Marco.Core.Wmi.WmiCredential { Domain = ".", Username = "admin" };
        Assert.True(dotLocal.IsLocalAccount("SERVER01"));
    }

    [Fact]
    public void IsLocalAccount_TreatsMicrosoftAccountAndAzureAdAsTokenFiltered()
    {
        var msa = new Marco.Core.Wmi.WmiCredential { Domain = "MicrosoftAccount", Username = "me@example.com" };
        Assert.True(msa.IsLocalAccount("SERVER01"));
        Assert.True(msa.IsMicrosoftAccount);

        var aad = new Marco.Core.Wmi.WmiCredential { Domain = "AzureAD", Username = "me@corp.com" };
        Assert.True(aad.IsLocalAccount("SERVER01"));
        Assert.False(aad.IsMicrosoftAccount);

        Assert.True(Marco.Core.Wmi.WmiCredential.IsPseudoLocalDomain("microsoftaccount"));
        Assert.False(Marco.Core.Wmi.WmiCredential.IsPseudoLocalDomain("CORP"));
    }
}
