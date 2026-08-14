using Marco.Core.Wmi;
using Xunit;
using static Marco.Tests.WmiFakeBuilders;

namespace Marco.Tests;

public class WmiObjectTests
{
    [Fact]
    public void ParsesCimDateTime()
    {
        var o = Obj(("d", "20240115143005.000000+000"));
        Assert.Equal(new DateTime(2024, 1, 15, 14, 30, 5), o.GetDateTime("d"));
    }

    [Fact]
    public void ParsesYyyyMmddDate()
        => Assert.Equal(new DateTime(2023, 6, 7), WmiObject.ParseCimDateTime("20230607"));

    [Fact]
    public void CoercesNumericStringsAndBoxedValues()
    {
        var o = Obj(("i", "42"), ("u", (ulong)9_000_000_000), ("b", true), ("bs", "true"));
        Assert.Equal(42, o.GetInt("i"));
        Assert.Equal(9_000_000_000UL, o.GetULong("u"));
        Assert.True(o.GetBool("b"));
        Assert.True(o.GetBool("bs"));
    }

    [Fact]
    public void MissingProperty_ReturnsNull()
    {
        var o = Obj(("x", "1"));
        Assert.Null(o.GetString("missing"));
        Assert.Null(o.GetInt("missing"));
        Assert.Null(o.GetDateTime("missing"));
        Assert.False(o.Has("missing"));
    }

    [Fact]
    public void EmptyString_IsTreatedAsNull()
        => Assert.Null(Obj(("s", "")).GetString("s"));

    [Fact]
    public void StringArray_JoinsForDisplayButExposesArray()
    {
        var o = Obj(("arr", new[] { "a", "b" }));
        Assert.Equal("a, b", o.GetString("arr"));
        Assert.Equal(new[] { "a", "b" }, o.GetStringArray("arr"));
    }
}
