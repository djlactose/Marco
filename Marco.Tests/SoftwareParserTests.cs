using Marco.Core.Inventory;
using Marco.Core.Model;
using Xunit;
using static Marco.Tests.WmiFakeBuilders;

namespace Marco.Tests;

public class SoftwareParserTests
{
    private static SoftwareParser.RawEntry Entry(SoftwareSource source, DateTime? lastWrite, params (string, object?)[] values)
        => new(Key("k" + Guid.NewGuid().ToString("N")[..6], lastWrite, values), source);

    [Fact]
    public void SkipsEntriesWithNoDisplayName()
    {
        var raw = new[]
        {
            Entry(SoftwareSource.Native64, null, ("DisplayVersion", "1.0")),        // no name
            Entry(SoftwareSource.Native64, null, ("DisplayName", "Real App"), ("DisplayVersion", "1.0")),
        };
        var result = SoftwareParser.Parse(raw, true);
        Assert.Single(result);
        Assert.Equal("Real App", result[0].DisplayName);
    }

    [Fact]
    public void SkipsSystemComponents()
    {
        var raw = new[]
        {
            Entry(SoftwareSource.Native64, null, ("DisplayName", "Hidden"), ("SystemComponent", 1)),
            Entry(SoftwareSource.Native64, null, ("DisplayName", "Visible")),
        };
        var result = SoftwareParser.Parse(raw, true);
        Assert.Single(result);
        Assert.Equal("Visible", result[0].DisplayName);
    }

    [Fact]
    public void SkipsUpdatesByParentKeyAndReleaseType()
    {
        var raw = new[]
        {
            Entry(SoftwareSource.Native64, null, ("DisplayName", "KB Update"), ("ParentKeyName", "SomeParent")),
            Entry(SoftwareSource.Native64, null, ("DisplayName", "Security Patch"), ("ReleaseType", "Security Update")),
            Entry(SoftwareSource.Native64, null, ("DisplayName", "Real Product")),
        };
        var result = SoftwareParser.Parse(raw, true);
        Assert.Single(result);
        Assert.Equal("Real Product", result[0].DisplayName);
    }

    [Fact]
    public void DeduplicatesOnNameVersionPublisher_KeepingFirstSource()
    {
        var raw = new[]
        {
            Entry(SoftwareSource.Native64, null, ("DisplayName", "App"), ("DisplayVersion", "2.0"), ("Publisher", "Acme")),
            Entry(SoftwareSource.Wow6432, null, ("DisplayName", "App"), ("DisplayVersion", "2.0"), ("Publisher", "Acme")),
        };
        var result = SoftwareParser.Parse(raw, true);
        Assert.Single(result);
        Assert.Equal(SoftwareSource.Native64, result[0].Source);
    }

    [Fact]
    public void DifferentVersions_AreNotDeduplicated()
    {
        var raw = new[]
        {
            Entry(SoftwareSource.Native64, null, ("DisplayName", "App"), ("DisplayVersion", "1.0")),
            Entry(SoftwareSource.Native64, null, ("DisplayName", "App"), ("DisplayVersion", "2.0")),
        };
        Assert.Equal(2, SoftwareParser.Parse(raw, true).Count);
    }

    [Fact]
    public void InstallDate_PrefersYyyyMmddValue()
    {
        var values = new Dictionary<string, object?> { ["InstallDate"] = "20230607" };
        var date = SoftwareParser.ResolveInstallDate(values, new DateTime(2020, 1, 1), true);
        Assert.Equal(new DateTime(2023, 6, 7), date);
    }

    [Fact]
    public void InstallDate_FallsBackToKeyLastWrite_WhenAvailable()
    {
        var values = new Dictionary<string, object?>(); // no InstallDate
        var lw = new DateTime(2021, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var date = SoftwareParser.ResolveInstallDate(values, lw, lastWriteAvailable: true);
        Assert.Equal(lw, date);
    }

    [Fact]
    public void InstallDate_IgnoresLastWrite_OnStdRegProvPath()
    {
        var values = new Dictionary<string, object?>();
        var date = SoftwareParser.ResolveInstallDate(values, new DateTime(2021, 5, 4), lastWriteAvailable: false);
        Assert.Null(date);
    }

    [Fact]
    public void InstallDate_ParsesInstallTimeFiletime()
    {
        var ft = new DateTime(2022, 3, 3, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var values = new Dictionary<string, object?> { ["InstallTime"] = (ulong)ft };
        var date = SoftwareParser.ResolveInstallDate(values, null, true);
        Assert.Equal(new DateTime(2022, 3, 3, 9, 0, 0, DateTimeKind.Utc), date!.Value.ToUniversalTime());
    }

    [Fact]
    public void ResultsAreSortedByName()
    {
        var raw = new[]
        {
            Entry(SoftwareSource.Native64, null, ("DisplayName", "Zeta")),
            Entry(SoftwareSource.Native64, null, ("DisplayName", "Alpha")),
        };
        var result = SoftwareParser.Parse(raw, true);
        Assert.Equal("Alpha", result[0].DisplayName);
        Assert.Equal("Zeta", result[1].DisplayName);
    }
}
