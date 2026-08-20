using Marco.Core.Cli;
using Marco.Core.Scanning;
using Xunit;

namespace Marco.Tests;

public class CliArgumentParserTests
{
    private static CliOptions Ok(params string[] args)
    {
        var result = CliArgumentParser.Parse(args);
        Assert.IsType<CliOptions>(result);
        return (CliOptions)result;
    }

    private static string Error(params string[] args)
    {
        var result = CliArgumentParser.Parse(args);
        return Assert.IsType<CliParseError>(result).Message;
    }

    [Fact]
    public void Minimal_TargetsAndOut()
    {
        var o = Ok("--targets", "10.0.0.0/24", "--out", "scan.json");
        Assert.Equal("10.0.0.0/24", o.TargetsValue);
        Assert.Equal("scan.json", o.OutJsonPath);
        Assert.False(o.NoInventory);
    }

    [Fact]
    public void RequiresTargets() => Assert.Contains("--targets is required", Error("--out", "x.json"));

    [Fact]
    public void RequiresAnOutput() => Assert.Contains("--out or --csv is required", Error("--targets", "10.0.0.1"));

    [Fact]
    public void UnknownFlag_IsRejected() => Assert.Contains("Unknown argument '--frobnicate'",
        Error("--targets", "x", "--out", "y", "--frobnicate"));

    [Fact]
    public void MissingValue_IsRejected() => Assert.Contains("--targets needs a value", Error("--out", "y.json", "--targets"));

    [Fact]
    public void Collectors_ValidatedAgainstCatalog()
    {
        var o = Ok("--targets", "x", "--out", "y", "--collectors", "System,Cpu,Memory");
        Assert.Equal(new[] { "System", "Cpu", "Memory" }, o.CollectorNames);

        var err = Error("--targets", "x", "--out", "y", "--collectors", "System,Nonsense");
        Assert.Contains("Unknown collector(s): Nonsense", err);
    }

    [Fact]
    public void Concurrency_ClampedToMax()
    {
        var o = Ok("--targets", "x", "--out", "y", "--concurrency", "99999");
        Assert.Equal(ConcurrencyLimits.Max, o.Concurrency);

        Assert.Contains("positive integer", Error("--targets", "x", "--out", "y", "--concurrency", "0"));
        Assert.Contains("positive integer", Error("--targets", "x", "--out", "y", "--concurrency", "abc"));
    }

    [Fact]
    public void NoInventory_ConflictsWithCollectors()
        => Assert.Contains("no effect with --no-inventory",
            Error("--targets", "x", "--out", "y", "--no-inventory", "--collectors", "System"));

    [Fact]
    public void AllFlags_Parse()
    {
        var o = Ok("--targets", "hosts.txt", "--out", "o.json", "--csv", "c:\\out",
            "--concurrency", "16", "--credential-label", "corp", "--client", "Acme",
            "--exit-code-on-change", "--quiet", "--log", "run.log");
        Assert.Equal("hosts.txt", o.TargetsValue);
        Assert.Equal("c:\\out", o.CsvDirectory);
        Assert.Equal(16, o.Concurrency);
        Assert.Equal("corp", o.CredentialLabel);
        Assert.Equal("Acme", o.ClientName);
        Assert.True(o.Quiet);
        Assert.True(o.ExitCodeOnChange);
        Assert.Equal("run.log", o.LogPath);
    }

    [Fact]
    public void CsvOnly_IsValid()
    {
        var o = Ok("--targets", "x", "--csv", "out");
        Assert.Null(o.OutJsonPath);
        Assert.Equal("out", o.CsvDirectory);
    }
}
