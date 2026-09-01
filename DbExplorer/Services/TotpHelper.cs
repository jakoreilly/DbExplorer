using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DbExplorer.Services;

/// <summary>
/// RFC 6238 (TOTP) / RFC 4226 (HOTP) with RFC 4648 base32 secrets, implemented on
/// <see cref="System.Security.Cryptography"/> alone so it adds no NuGet dependency —
/// same rule as <see cref="BCryptHelper"/>.
///
/// Scope: this is the second factor for the built-in local credential store only
/// (<c>Auth:Local</c>). Windows Negotiate, Google OAuth and Bastion OIDC each do
/// their own MFA upstream, and the MCP bearer-token path is non-interactive — none
/// of them go through here.
///
/// Parameters are the near-universal authenticator-app defaults: SHA-1, 30-second
/// step, 6 digits. A verify accepts the step before and after the current one
/// (<c>window: 1</c>) to tolerate clock skew, which is the standard trade-off.
/// </summary>
public static class TotpHelper
{
    private const int StepSeconds = 30;
    private const int Digits = 6;

    /// <summary>Generates a fresh 160-bit secret, returned base32-encoded for an authenticator app.</summary>
    public static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    /// <summary>
    /// Verifies <paramref name="code"/> against <paramref name="base32Secret"/> for the
    /// current time, accepting +/- <paramref name="window"/> steps of clock skew.
    /// Returns false for any malformed input rather than throwing.
    /// </summary>
    public static bool Verify(string base32Secret, string? code, int window = 1)
        => Verify(base32Secret, code, DateTimeOffset.UtcNow, window);

    /// <summary>Testable overload — <paramref name="now"/> is the reference time.</summary>
    public static bool Verify(string base32Secret, string? code, DateTimeOffset now, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
            return false;

        code = code.Trim();
        if (code.Length != Digits || !code.All(char.IsDigit))
            return false;

        byte[] key;
        try { key = Base32Decode(base32Secret); }
        catch { return false; }
        if (key.Length == 0) return false;

        long counter = now.ToUnixTimeSeconds() / StepSeconds;
        bool ok = false;
        // Check the whole window even after a match so timing does not leak which step hit.
        for (long i = counter - window; i <= counter + window; i++)
        {
            var expected = ComputeCode(key, i < 0 ? 0 : i);
            if (FixedTimeEquals(expected, code)) ok = true;
        }
        return ok;
    }

    /// <summary>The current 6-digit code, for enrollment self-check / diagnostics.</summary>
    public static string CurrentCode(string base32Secret, DateTimeOffset? now = null)
    {
        var key = Base32Decode(base32Secret);
        long counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;
        return ComputeCode(key, counter);
    }

    /// <summary>
    /// The <c>otpauth://totp/…</c> URI an authenticator app imports (usually via QR).
    /// </summary>
    public static string BuildOtpAuthUri(string base32Secret, string account, string issuer = "DbExplorer")
    {
        // otpauth label is "<issuer>:<account>" with a literal colon separator; the two
        // parts are percent-encoded individually (Google Authenticator's Key URI format).
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";
        var iss = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={iss}"
             + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    // ── HOTP (RFC 4226 §5.3) ─────────────────────────────────────────────────
    private static string ComputeCode(byte[] key, long counter)
    {
        Span<byte> msg = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(msg, counter);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, msg, hash);

        int offset = hash[19] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        int otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString(new string('0', Digits));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // ── base32, RFC 4648 §6, no padding required on input ────────────────────
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0)
            sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        input = input.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();

        var bytes = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException($"Invalid base32 character '{c}'.");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return [.. bytes];
    }
}
