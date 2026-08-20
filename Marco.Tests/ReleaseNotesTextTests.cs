using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

public class ReleaseNotesTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void EmptyInput_YieldsEmpty(string? input)
        => Assert.Equal("", ReleaseNotesText.Clean(input));

    [Fact]
    public void GitHubGeneratedChangelog_CleansToReadableText()
    {
        // The exact shape `gh release create --generate-notes` produces.
        var body = "## What's Changed\r\n"
                 + "* Cap scan concurrency at what this PC can sustain by @djlactose in https://github.com/djlactose/Marco/pull/12\r\n"
                 + "* Ask before updating; group the results grid by @djlactose in https://github.com/djlactose/Marco/pull/13\r\n"
                 + "\r\n"
                 + "**Full Changelog**: https://github.com/djlactose/Marco/compare/v1.0.9...v1.1.0";

        Assert.Equal(
            "What's Changed\n"
            + "• Cap scan concurrency at what this PC can sustain\n"
            + "• Ask before updating; group the results grid",
            ReleaseNotesText.Clean(body));
    }

    [Fact]
    public void Headers_KeepTheirText()
        => Assert.Equal("Fixes", ReleaseNotesText.Clean("### Fixes"));

    [Theory]
    [InlineData("- one", "• one")]
    [InlineData("* two", "• two")]
    [InlineData("+ three", "• three")]
    [InlineData("  - nested", "  • nested")]
    public void Bullets_BecomeDots(string input, string expected)
        => Assert.Equal(expected, ReleaseNotesText.Clean(input));

    [Fact]
    public void Links_CollapseToTheirText()
        => Assert.Equal("See the docs for details.",
            ReleaseNotesText.Clean("See [the docs](https://example.com/docs) for details."));

    [Fact]
    public void Images_AreDropped()
        => Assert.Equal("Before after", ReleaseNotesText.Clean("Before ![screenshot](https://x/i.png) after"));

    [Fact]
    public void BoldAndCode_MarkersStripped()
        => Assert.Equal("Stop is instant now, honest.",
            ReleaseNotesText.Clean("**Stop** is `instant` now, __honest__."));

    [Fact]
    public void AttributionWithoutUrl_AlsoStripped()
        => Assert.Equal("• Fix the thing", ReleaseNotesText.Clean("* Fix the thing by @someone"));

    [Fact]
    public void BlankLineRuns_Collapse()
        => Assert.Equal("a\n\nb", ReleaseNotesText.Clean("a\n\n\n\n\nb"));

    [Fact]
    public void HtmlComments_AreDropped()
        => Assert.Equal("visible", ReleaseNotesText.Clean("<!-- release-bot: ignore -->visible"));
}
