using System.Security.Cryptography;

namespace AIOPatcher.Server.Security;

/// <summary>TOTP nach RFC 6238 (HMAC-SHA1, 6 Stellen, 30 Sekunden), kompatibel mit allen gängigen Authenticator-Apps.</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    public static long GetTimeStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    public static string ComputeCode(byte[] secret, long timeStep, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        var mod = (int)Math.Pow(10, digits);
        return (binary % mod).ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// Prüft einen Code mit ±1 Zeitschritt Toleranz. Liefert den passenden Zeitschritt,
    /// damit der Aufrufer bereits benutzte Codes ablehnen kann.
    /// </summary>
    public static bool TryVerify(byte[] secret, string? code, DateTimeOffset now, long lastUsedTimeStep, out long matchedTimeStep)
    {
        matchedTimeStep = 0;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        code = code.Replace(" ", "", StringComparison.Ordinal);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var current = GetTimeStep(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step <= lastUsedTimeStep)
            {
                continue;
            }

            var expected = ComputeCode(secret, step);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expected),
                    System.Text.Encoding.ASCII.GetBytes(code)))
            {
                matchedTimeStep = step;
                return true;
            }
        }

        return false;
    }

    public static string BuildOtpAuthUri(string issuer, string account, byte[] secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }
}

public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return sb.ToString();
    }
}
