using ParityProof.Core.Models;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class BuildInfoTests
{
    [Fact]
    public void Current_ShouldNotBeNull()
    {
        BuildInfo info = BuildInfo.Current;

        Assert.NotNull(info);
        Assert.False(string.IsNullOrWhiteSpace(info.SemVer));
        Assert.DoesNotContain("+", info.SemVer);
        Assert.NotNull(info.CommitSha);
        Assert.True(info.CommitSha.Length <= 7);
        Assert.StartsWith("ParityProof v", info.DisplayVersion);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", "unknown")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1", "unknown")]
    [InlineData("1.2.3-rc.1+abcdef0123456789", "1.2.3-rc.1", "abcdef0")]
    [InlineData("2.0.0-beta.2+1234567", "2.0.0-beta.2", "1234567")]
    [InlineData("2.0.0-alpha+abc", "2.0.0-alpha", "abc")]
    [InlineData(null, "1.0.0-dev", "unknown")]
    [InlineData("", "1.0.0-dev", "unknown")]
    [InlineData("   ", "1.0.0-dev", "unknown")]
    public void Parse_ShouldExtractSemVerAndCommitShaAccurately(
        string? rawVersion,
        string expectedSemVer,
        string expectedCommitSha)
    {
        BuildInfo info = BuildInfo.Parse(rawVersion, isDebug: false);

        Assert.Equal(expectedSemVer, info.SemVer);
        Assert.Equal(expectedCommitSha, info.CommitSha);

        if (expectedCommitSha != "unknown")
        {
            Assert.Equal($"ParityProof v{expectedSemVer} ({expectedCommitSha})", info.DisplayVersion);
        }
        else
        {
            Assert.Equal($"ParityProof v{expectedSemVer}", info.DisplayVersion);
        }
    }

    [Fact]
    public void Parse_WithDebugFlag_ShouldReflectIsDebug()
    {
        BuildInfo info = BuildInfo.Parse("1.0.0", isDebug: true);

        Assert.True(info.IsDebug);
    }
}
