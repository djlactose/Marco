using Marco.Core.Inventory;
using Marco.Inventory.Registry;
using Xunit;
using static Marco.Tests.WmiFakeBuilders;

namespace Marco.Tests;

public class FallbackRegistryTests
{
    private const string Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [Fact]
    public void UsesPrimary_WhenItSucceeds()
    {
        var primary = new FakeRemoteRegistry { SupportsLastWriteTime = true };
        primary.Subkeys[$"LocalMachine:{Path}"] = new() { Key("app", null, ("DisplayName", "FromPrimary")) };
        var fallback = new FakeRemoteRegistry();

        var reg = new FallbackRemoteRegistry(primary, fallback);
        var keys = reg.EnumerateSubkeys(RegistryRoot.LocalMachine, Path, new[] { "DisplayName" });

        Assert.Single(keys);
        Assert.Equal("FromPrimary", keys[0].Values["DisplayName"]);
        Assert.True(reg.SupportsLastWriteTime);
    }

    [Fact]
    public void FallsBackToStdRegProv_WhenPrimaryThrows()
    {
        var primary = new FakeRemoteRegistry { ThrowOnAccess = true, SupportsLastWriteTime = true }; // SMB/service down
        var fallback = new FakeRemoteRegistry { SupportsLastWriteTime = false };                     // StdRegProv
        fallback.Subkeys[$"LocalMachine:{Path}"] = new() { Key("app", null, ("DisplayName", "FromFallback")) };

        var reg = new FallbackRemoteRegistry(primary, fallback);
        var keys = reg.EnumerateSubkeys(RegistryRoot.LocalMachine, Path, new[] { "DisplayName" });

        Assert.Single(keys);
        Assert.Equal("FromFallback", keys[0].Values["DisplayName"]);
        // Once we've fallen back, last-write times are no longer available.
        Assert.False(reg.SupportsLastWriteTime);
    }

    [Fact]
    public void StaysOnFallback_AfterFirstPrimaryFailure()
    {
        var primary = new FakeRemoteRegistry { ThrowOnAccess = true };
        var fallback = new FakeRemoteRegistry();
        fallback.SubkeyNames["Users:"] = new() { "S-1-5-21-1" };

        var reg = new FallbackRemoteRegistry(primary, fallback);
        reg.EnumerateSubkeys(RegistryRoot.LocalMachine, Path, new[] { "DisplayName" }); // trips the switch
        var names = reg.GetSubKeyNames(RegistryRoot.Users, "");                          // should use fallback directly

        Assert.Contains("S-1-5-21-1", names);
    }
}
