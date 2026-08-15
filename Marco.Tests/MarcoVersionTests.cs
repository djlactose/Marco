using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

public class MarcoVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V1.2", 1, 2, 0, null)]
    [InlineData("1.2.3.4", 1, 2, 3, null)]
    [InlineData("1.2.3-beta.4", 1, 2, 3, 4)]
    [InlineData("v1.2.3-beta.0", 1, 2, 3, 0)]
    [InlineData("v10.20.30-BETA.99", 10, 20, 30, 99)]
    public void TryParse_Accepts(string text, int major, int minor, int build, int? beta)
    {
        Assert.True(MarcoVersion.TryParse(text, out var v));
        Assert.Equal(major, v.Core.Major);
        Assert.Equal(minor, v.Core.Minor);
        Assert.Equal(build, v.Core.Build);
        Assert.Equal(beta, v.Beta);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v1")]                 // Version.TryParse needs major.minor
    [InlineData("v1.2.3-rc1")]         // only -beta.N is installable
    [InlineData("v1.2.3-alpha.1")]
    [InlineData("v1.2.3-beta")]        // no number
    [InlineData("v1.2.3-beta.x")]
    [InlineData("v1.2.3-beta.-1")]
    [InlineData("not-a-version")]
    public void TryParse_Rejects(string? text)
    {
        Assert.False(MarcoVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.9")]           // numeric, not lexicographic
    [InlineData("1.2.0", "1.2.0-beta.9")]     // stable outranks its own betas
    [InlineData("1.2.0-beta.10", "1.2.0-beta.9")]
    [InlineData("1.2.0-beta.1", "1.1.0")]     // a beta of the NEXT version outranks the previous stable
    [InlineData("2.0.0", "1.99.99")]
    public void Ordering_GreaterThan(string left, string right)
    {
        Assert.True(MarcoVersion.TryParse(left, out var a));
        Assert.True(MarcoVersion.TryParse(right, out var b));
        Assert.True(a > b);
        Assert.True(b < a);
        Assert.False(b >= a);
    }

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.0-beta.4", "v1.2.0-beta.4")]
    public void Ordering_Equal(string left, string right)
    {
        Assert.True(MarcoVersion.TryParse(left, out var a));
        Assert.True(MarcoVersion.TryParse(right, out var b));
        Assert.Equal(0, a.CompareTo(b));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.4", "1.2.3-beta.4")]
    [InlineData("1.2", "1.2.0")]
    public void ToString_RoundTripsDisplayForm(string input, string expected)
    {
        Assert.True(MarcoVersion.TryParse(input, out var v));
        Assert.Equal(expected, v.ToString());
    }
}
