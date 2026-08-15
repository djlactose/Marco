using System.Security.Cryptography;
using System.Text;
using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

public class Sha256FileTests
{
    private const string Hex = "a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3"; // sha256("123\n")... any 64-hex

    [Theory]
    [InlineData(Hex + "  Marco.exe")]                 // publish.ps1 format
    [InlineData(Hex)]                                 // bare hash
    [InlineData("  " + Hex + "  Marco.exe\r\n")]      // padding + newline
    public void ParseChecksumText_ExtractsHash(string text)
    {
        Assert.Equal(Hex, Sha256File.ParseChecksumText(text));
    }

    [Fact]
    public void ParseChecksumText_LowercasesUppercaseInput()
    {
        Assert.Equal(Hex, Sha256File.ParseChecksumText(Hex.ToUpperInvariant() + "  Marco.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("deadbeef  Marco.exe")]               // too short
    [InlineData("zz65a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3")] // non-hex
    public void ParseChecksumText_RejectsGarbage(string? text)
    {
        Assert.Null(Sha256File.ParseChecksumText(text));
    }

    [Fact]
    public void ComputeAndVerify_AgainstRealFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"marco-test-{Guid.NewGuid():N}.bin");
        try
        {
            var payload = Encoding.UTF8.GetBytes("marco update payload");
            File.WriteAllBytes(path, payload);
            var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            Assert.Equal(expected, Sha256File.ComputeLowerHex(path));
            Assert.True(Sha256File.Verify(path, expected));
            Assert.True(Sha256File.Verify(path, expected.ToUpperInvariant())); // case-insensitive
            Assert.False(Sha256File.Verify(path, new string('0', 64)));
            Assert.False(Sha256File.Verify(path, null));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Verify_MissingFile_IsFalseNotThrow()
    {
        Assert.False(Sha256File.Verify(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"), Hex));
    }
}
