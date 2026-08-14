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
