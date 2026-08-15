using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

public class GitHubReleaseParserTests
{
    private const string Sha = "a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3";

    private static string Release(string tag, bool prerelease = false, bool draft = false,
        bool withExe = true, bool withSha = true) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "Marco {{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/djlactose/Marco/releases/tag/{{tag}}",
          "body": "## What's new\n- things",
          "assets": [
            {{(withExe ? $$"""{ "name": "Marco.exe", "size": 79000000, "browser_download_url": "https://github.com/djlactose/Marco/releases/download/{{tag}}/Marco.exe" },""" : "")}}
            {{(withSha ? $$"""{ "name": "Marco.exe.sha256", "size": 76, "browser_download_url": "https://github.com/djlactose/Marco/releases/download/{{tag}}/Marco.exe.sha256" },""" : "")}}
            { "name": "decoy.zip", "size": 10, "browser_download_url": "https://example.invalid/decoy.zip" }
          ]
        }
        """;

    [Fact]
    public void ParseRelease_ReadsEverything()
    {
        var release = GitHubReleaseParser.ParseRelease(Release("v1.2.0"));

        Assert.NotNull(release);
        Assert.Equal("v1.2.0", release.TagName);
        Assert.Equal("1.2.0", release.Version.ToString());
        Assert.False(release.IsPrerelease);
        Assert.Equal(79000000, release.ExeSizeBytes);
        Assert.Contains("/v1.2.0/Marco.exe", release.ExeDownloadUrl);
        Assert.Contains("/v1.2.0/Marco.exe.sha256", release.Sha256DownloadUrl);
        Assert.Contains("/releases/tag/v1.2.0", release.HtmlUrl);
        Assert.Contains("What's new", release.Body);
    }

    [Theory]
    [InlineData(true, false)]   // draft
    [InlineData(false, true)]   // missing exe asset
    public void ParseRelease_RejectsUnusable(bool draft, bool dropExe)
    {
        var json = Release("v1.2.0", draft: draft, withExe: !dropExe);
        Assert.Null(GitHubReleaseParser.ParseRelease(json));
    }

    [Fact]
    public void ParseRelease_RejectsMissingChecksumAsset()
    {
        Assert.Null(GitHubReleaseParser.ParseRelease(Release("v1.2.0", withSha: false)));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v1.2.0-rc1")]
    public void ParseRelease_RejectsUnparseableTags(string tag)
    {
        Assert.Null(GitHubReleaseParser.ParseRelease(Release(tag)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"half\": ")]
    [InlineData("42")]
    [InlineData("null")]
    public void ParseRelease_MalformedInput_ReturnsNullNotThrow(string json)
    {
        Assert.Null(GitHubReleaseParser.ParseRelease(json));
    }

    [Fact]
    public void ParseLatestFromList_PicksHighestVersion_AcrossStableAndBeta()
    {
        // Beta channel semantics: the newest thing wins, whether stable or beta.
        var json = $"[{Release("v1.1.0")}, {Release("v1.2.0-beta.3", prerelease: true)}, {Release("v1.2.0-beta.11", prerelease: true)}]";
        var best = GitHubReleaseParser.ParseLatestFromList(json);

        Assert.NotNull(best);
        Assert.Equal("v1.2.0-beta.11", best.TagName); // beta.11 > beta.3 numerically, not lexicographically
        Assert.True(best.IsPrerelease);
    }

    [Fact]
    public void ParseLatestFromList_NewStableOutranksOlderBetas()
    {
        var json = $"[{Release("v1.2.0-beta.7", prerelease: true)}, {Release("v1.2.0")}]";
        var best = GitHubReleaseParser.ParseLatestFromList(json);

        Assert.NotNull(best);
        Assert.Equal("v1.2.0", best.TagName);
    }

    [Fact]
    public void ParseLatestFromList_SkipsUnusableEntries()
    {
        var json = $"[{Release("v9.9.9", draft: true)}, {Release("bogus-tag")}, {Release("v1.0.5")}]";
        var best = GitHubReleaseParser.ParseLatestFromList(json);

        Assert.NotNull(best);
        Assert.Equal("v1.0.5", best.TagName);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]          // object where an array is expected
    [InlineData("broken [")]
    public void ParseLatestFromList_EmptyOrMalformed_ReturnsNull(string json)
    {
        Assert.Null(GitHubReleaseParser.ParseLatestFromList(json));
    }
}
