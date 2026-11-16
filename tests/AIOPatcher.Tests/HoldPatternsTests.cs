using AIOPatcher.Contracts;

namespace AIOPatcher.Tests;

public sealed class HoldPatternsTests
{
    [Theory]
    [InlineData("linux-image-*", "linux-image-6.8.0-45-generic", true)]
    [InlineData("linux-image-*", "linux-headers-6.8.0", false)]
    [InlineData("KB5034441", "kb5034441", true)]
    [InlineData("*Preview*", "2026-11 Cumulative Update Preview for Windows", true)]
    [InlineData("openssl", "openssl-dev", false)]
    [InlineData("lib?sl3", "libssl3", true)]
    [InlineData("lib?ssl3", "libssl3", false)]
    [InlineData("*", "anything", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    public void Matches_Glob(string pattern, string text, bool expected) => Assert.Equal(expected, HoldPatterns.Matches(pattern, text));

    [Fact]
    public void IsHeld_ChecksAllCandidates_AndIgnoresEmpty()
    {
        Assert.True(HoldPatterns.IsHeld(["KB5034441"], "Some title", "KB5034441"));
        Assert.False(HoldPatterns.IsHeld([], "x"));
        Assert.False(HoldPatterns.IsHeld(null, "x"));
        Assert.False(HoldPatterns.IsHeld(["x"], null, ""));
    }
}
