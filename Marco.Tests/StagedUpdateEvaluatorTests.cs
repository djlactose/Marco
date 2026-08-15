using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

public class StagedUpdateEvaluatorTests
{
    private const string GoodHash = "a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3";

    private static MarcoVersion V(string s)
    {
        Assert.True(MarcoVersion.TryParse(s, out var v));
        return v;
    }

    private static StagedManifest Manifest(string version, string sha = GoodHash)
        => new(version, $"Marco-{version}.exe", sha, DateTime.UtcNow);

    private static StagedDecision Evaluate(string current, StagedManifest? manifest,
        bool includeBeta = true, bool fileExists = true, string? actualHash = GoodHash)
        => StagedUpdateEvaluator.Evaluate(
            V(current), includeBeta, manifest,
            _ => fileExists,
            _ => actualHash ?? throw new IOException("unreadable"));

    [Fact]
    public void NewerVerifiedStaged_IsApplied()
        => Assert.Equal(StagedDecision.Apply, Evaluate("1.1.0", Manifest("1.2.0")));

    [Fact]
    public void NewerBeta_IsApplied_OnBetaChannel()
        => Assert.Equal(StagedDecision.Apply, Evaluate("1.1.0", Manifest("1.2.0-beta.4")));

    [Fact]
    public void StagedBeta_OnStableChannel_IsDiscarded()
        => Assert.Equal(StagedDecision.StaleOlderOrEqual, Evaluate("1.1.0", Manifest("1.2.0-beta.4"), includeBeta: false));

    [Theory]
    [InlineData("1.2.0", "1.2.0")]   // equal
    [InlineData("1.2.0", "1.1.0")]   // older — never downgrade
    [InlineData("1.2.0", "1.2.0-beta.9")] // beta of the running stable
    public void EqualOrOlderStaged_IsStale(string current, string staged)
        => Assert.Equal(StagedDecision.StaleOlderOrEqual, Evaluate(current, Manifest(staged)));

    [Fact]
    public void MissingFile_IsCorrupt()
        => Assert.Equal(StagedDecision.CorruptOrMissing, Evaluate("1.1.0", Manifest("1.2.0"), fileExists: false));

    [Fact]
    public void HashMismatch_IsCorrupt()
        => Assert.Equal(StagedDecision.CorruptOrMissing,
            Evaluate("1.1.0", Manifest("1.2.0"), actualHash: new string('0', 64)));

    [Fact]
    public void UnreadableFile_IsCorrupt_NotThrow()
        => Assert.Equal(StagedDecision.CorruptOrMissing, Evaluate("1.1.0", Manifest("1.2.0"), actualHash: null));

    [Fact]
    public void NullManifest_IsNone()
        => Assert.Equal(StagedDecision.None, Evaluate("1.1.0", null));

    [Fact]
    public void UnparseableManifestVersion_IsNone()
        => Assert.Equal(StagedDecision.None, Evaluate("1.1.0", Manifest("garbage")));

    [Fact]
    public void HashComparison_IsCaseInsensitive()
        => Assert.Equal(StagedDecision.Apply,
            Evaluate("1.1.0", Manifest("1.2.0", GoodHash.ToUpperInvariant())));
}
