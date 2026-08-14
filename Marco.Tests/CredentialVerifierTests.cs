using Marco.Core.Wmi;
using Marco.Credentials;
using Xunit;

namespace Marco.Tests;

public class CredentialVerifierTests
{
    private static CredentialSet Set(string user = "admin", string? domain = "CORP")
    {
        var s = new CredentialSet(user, domain, user, null);
        s.SetPassword("pw");
        return s;
    }

    [Fact]
    public async Task VerifyAgainstHost_Success_WhenConnectAndQuerySucceed()
    {
        var factory = new FakeWmiSessionFactory((h, _) =>
            new FakeWmiSession(h).With("Win32_ComputerSystem", WmiFakeBuilders.Obj(("Name", h))));
        var verifier = new CredentialVerifier(factory);

        var result = await verifier.VerifyAgainstHostAsync(Set(), "server01", default);

        Assert.True(result.Success);
        Assert.Equal(VerifyOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task VerifyAgainstHost_MapsAuthFailure()
    {
        var factory = new FakeWmiSessionFactory((h, _) =>
            throw new WmiException(WmiFailureKind.AuthFailed, "bad creds"));
        var result = await new CredentialVerifier(factory).VerifyAgainstHostAsync(Set(), "server01", default);

        Assert.False(result.Success);
        Assert.Equal(VerifyOutcome.BadCredentials, result.Outcome);
    }

    [Fact]
    public async Task VerifyAgainstHost_SurfacesAccessDeniedHint()
    {
        var factory = new FakeWmiSessionFactory((h, _) =>
            throw new WmiException(WmiFailureKind.AccessDenied, "denied", hint: "Set LocalAccountTokenFilterPolicy=1"));
        var result = await new CredentialVerifier(factory).VerifyAgainstHostAsync(Set(domain: null), "server01", default);

        Assert.Equal(VerifyOutcome.AccessDenied, result.Outcome);
        Assert.Contains("LocalAccountTokenFilterPolicy", result.Hint);
    }

    [Fact]
    public async Task VerifyAgainstHost_MapsUnreachable()
    {
        var factory = new FakeWmiSessionFactory((h, _) =>
            throw new WmiException(WmiFailureKind.Unreachable, "RPC unavailable"));
        var result = await new CredentialVerifier(factory).VerifyAgainstHostAsync(Set(), "server01", default);

        Assert.Equal(VerifyOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAgainstHost_EmptyHost_IsError()
    {
        var factory = new FakeWmiSessionFactory((h, _) => new FakeWmiSession(h));
        var result = await new CredentialVerifier(factory).VerifyAgainstHostAsync(Set(), "  ", default);
        Assert.Equal(VerifyOutcome.Error, result.Outcome);
    }

    [Fact]
    public async Task VerifyAgainstLocalHost_WithAlternateCreds_FallsBackToLogon_NotFalseSuccess()
    {
        // Against the local machine, WMI ignores alternate creds — the verifier must NOT call the factory and
        // must not report a false success for a bogus credential.
        var factory = new FakeWmiSessionFactory((h, _) => new FakeWmiSession(h)
            .With("Win32_ComputerSystem", WmiFakeBuilders.Obj(("Name", h))));
        var bad = new CredentialSet("bad", ".", "marco-nonexistent-user-xyz", null);
        bad.SetPassword("definitely-wrong");

        var result = await new CredentialVerifier(factory).VerifyAgainstHostAsync(bad, "127.0.0.1", default);

        Assert.False(result.Success);                       // not a false positive
        Assert.Empty(factory.AttemptedUsernames);           // WMI path was skipped for the local host
    }

    [Fact]
    public async Task VerifyAgainstLocalHost_WithCurrentToken_UsesWmiPath()
    {
        var factory = new FakeWmiSessionFactory((h, _) => new FakeWmiSession(h)
            .With("Win32_ComputerSystem", WmiFakeBuilders.Obj(("Name", h))));
        var result = await new CredentialVerifier(factory)
            .VerifyAgainstHostAsync(CredentialSet.CurrentToken(), "127.0.0.1", default);

        Assert.True(result.Success);
        Assert.Single(factory.AttemptedUsernames); // current token → real (valid) local WMI test
    }

    [Fact]
    public void ValidateLogon_CurrentToken_IsTriviallyValid()
    {
        var result = CredentialVerifier.ValidateLogon(CredentialSet.CurrentToken());
        Assert.True(result.Success);
    }

    [Fact]
    public void ValidateLogon_WrongPassword_ReportsBadCredentials()
    {
        // A username/password that does not exist locally should fail LogonUser with a bad-credentials outcome.
        var set = new CredentialSet("bogus", ".", "marco-nonexistent-user-xyz", null);
        set.SetPassword("definitely-not-the-password");
        var result = CredentialVerifier.ValidateLogon(set);

        Assert.False(result.Success);
        Assert.Equal(VerifyOutcome.BadCredentials, result.Outcome);
    }
}
