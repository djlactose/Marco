using Marco.Core.Compliance;
using Marco.Core.Lifecycle;
using Marco.Core.Model;
using Xunit;

namespace Marco.Tests;

public class LifecycleTests
{
    private static readonly DateTime Today = new(2026, 8, 20);

    private static Machine Win(string build, string? edition = null, string? installationType = "Client",
        DeviceType type = DeviceType.Windows)
    {
        var m = new Machine("10.0.0.20") { DeviceType = type };
        m.Os.Build = build;
        m.Updates.EditionId = edition;
        m.Updates.InstallationType = installationType;
        return m;
    }

    [Fact]
    public void Table_LoadsWithBothPlatforms()
    {
        var t = OsEolTable.Data;
        Assert.True(t.Windows.Count >= 15);
        Assert.True(t.Linux.Count >= 10);
        Assert.False(string.IsNullOrEmpty(t.Updated));
    }

    [Fact]
    public void Win10_22H2_IsEndOfLife()
    {
        var info = LifecycleEvaluator.Evaluate(Win("19045", "Professional"), OsEolTable.Data, Today)!;
        Assert.Equal(OsSupportStatus.EndOfLife, info.OsSupport);
        Assert.Equal(new DateTime(2025, 10, 14), info.OsEndOfSupport);
        Assert.StartsWith("EOL", info.Display);
    }

    [Fact]
    public void Win11_24H2_IsEndingSoon_ForHomePro_ButSupportedForEnterprise()
    {
        // Home/Pro ends 2026-10-13 (< 180 days from Today); Enterprise runs to 2027-10-12.
        var pro = LifecycleEvaluator.Evaluate(Win("26100", "Professional"), OsEolTable.Data, Today)!;
        Assert.Equal(OsSupportStatus.EndingSoon, pro.OsSupport);

        var ent = LifecycleEvaluator.Evaluate(Win("26100", "Enterprise"), OsEolTable.Data, Today)!;
        Assert.Equal(OsSupportStatus.Supported, ent.OsSupport);
    }

    [Fact]
    public void Build17763_SeparatesWin10From_Server2019()
    {
        // Same build, different product lines, very different dates.
        var client = LifecycleEvaluator.Evaluate(Win("17763", "Professional"), OsEolTable.Data, Today)!;
        Assert.Equal(OsSupportStatus.EndOfLife, client.OsSupport);
        Assert.StartsWith("Windows 10", client.OsReleaseLabel);

        var server = LifecycleEvaluator.Evaluate(
            Win("17763", installationType: "Server", type: DeviceType.WindowsServer), OsEolTable.Data, Today)!;
        Assert.StartsWith("Windows Server 2019", server.OsReleaseLabel);
        Assert.Equal(OsSupportStatus.Supported, server.OsSupport); // extended runs to 2029
    }

    [Fact]
    public void Ubuntu_MatchesByCaption()
    {
        var m = new Machine("10.0.0.30") { DeviceType = DeviceType.UnixLinux };
        m.Os.Caption = "Ubuntu 20.04.6 LTS";
        var info = LifecycleEvaluator.Evaluate(m, OsEolTable.Data, Today)!;
        Assert.Equal(OsSupportStatus.EndOfLife, info.OsSupport); // standard support ended 2025-05
        Assert.Equal("Ubuntu 20.04", info.OsReleaseLabel);
    }

    [Fact]
    public void UnknownBuild_YieldsNullOrUnknown()
    {
        var m = Win("99999");
        Assert.Null(LifecycleEvaluator.Evaluate(m, OsEolTable.Data, Today)); // no bios date either → nothing to say

        m.System.BiosDate = Today.AddYears(-6);
        var info = LifecycleEvaluator.Evaluate(m, OsEolTable.Data, Today)!;
        Assert.Equal(OsSupportStatus.Unknown, info.OsSupport);
        Assert.Equal(6.0, info.HardwareAgeYears!.Value, 1); // age still reported
    }

    [Fact]
    public void OsSupportedRule_FailsOnlyOnEndOfLife()
    {
        (RuleStatus, string?) Run(Machine m) => RuleCheckCatalog.Checks["os-supported"](m, new Dictionary<string, int>());

        var eol = Win("19045", "Professional");
        eol.Lifecycle = LifecycleEvaluator.Evaluate(eol, OsEolTable.Data, Today);
        Assert.Equal(RuleStatus.Fail, Run(eol).Item1);

        var soon = Win("26100", "Professional");
        soon.Lifecycle = LifecycleEvaluator.Evaluate(soon, OsEolTable.Data, Today);
        Assert.Equal(RuleStatus.Pass, Run(soon).Item1);          // ending soon still passes...
        Assert.Contains("ends", Run(soon).Item2);                 // ...but the detail carries the date

        Assert.Equal(RuleStatus.Unknown, Run(Win("99999")).Item1); // no lifecycle → unknown
    }
}
