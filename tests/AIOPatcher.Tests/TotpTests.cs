using System.Text;
using AIOPatcher.Server.Security;

namespace AIOPatcher.Tests;

public class TotpTests
{
    // RFC 6238, Anhang B (SHA1-Secret "12345678901234567890").
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(20000000000L, "65353130")]
    public void ComputeCode_MatchesRfc6238Vectors(long unixTime, string expected)
    {
        var step = Totp.GetTimeStep(DateTimeOffset.FromUnixTimeSeconds(unixTime));
        Assert.Equal(expected, Totp.ComputeCode(RfcSecret, step, digits: 8));
    }

    [Fact]
    public void TryVerify_AcceptsCurrentCode_AndRejectsReuse()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var code = Totp.ComputeCode(RfcSecret, Totp.GetTimeStep(now));

        Assert.True(Totp.TryVerify(RfcSecret, code, now, lastUsedTimeStep: 0, out var step));
        Assert.False(Totp.TryVerify(RfcSecret, code, now, lastUsedTimeStep: step, out _));
    }

    [Fact]
    public void TryVerify_ToleratesOneStepClockSkew_ButNotTwo()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.GetTimeStep(now);

        Assert.True(Totp.TryVerify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 1), now, 0, out _));
        Assert.True(Totp.TryVerify(RfcSecret, Totp.ComputeCode(RfcSecret, step + 1), now, 0, out _));
        Assert.False(Totp.TryVerify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 2), now, 0, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("1234567")]
    public void TryVerify_RejectsMalformedCodes(string? code)
    {
        Assert.False(Totp.TryVerify(RfcSecret, code, DateTimeOffset.UtcNow, 0, out _));
    }

    [Fact]
    public void Base32_EncodesRfc4648Vector()
    {
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
    }
}
