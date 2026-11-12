using System.Security.Cryptography;
using System.Text;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using QRCoder;

namespace KaiZent.Platform.Api.Infrastructure;

/// <summary>Einmalcodes nach RFC 6238 (TOTP, HMAC-SHA1, 30 Sekunden, 6 Ziffern), wie sie Authenticator-Apps erzeugen.</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(string secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xff); step >>= 8; }
        var hash = HMACSHA1.HashData(Base32Decode(secret), counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Prüft den Code mit einem Schritt Toleranz in beide Richtungen. Liefert den benutzten Zeitschritt, der größer sein muss als
    /// <paramref name="lastUsedStep"/>: ein Code gilt nur einmal.
    /// </summary>
    public static long? Verify(string secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var clean = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (clean.Length != Digits) return null;
        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step <= (lastUsedStep ?? long.MinValue)) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(clean))) return step;
        }
        return null;
    }

    public static string OtpAuthUri(string secret, string account, string issuer) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    public static string QrSvg(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(5);
    }

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string s)
    {
        var clean = s.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var result = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Kein Base32.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { result.Add((byte)((buffer >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return result.ToArray();
    }
}

/// <summary>Wiederherstellungscodes: zehn zufällige Codes, gespeichert nur als SHA-256 (hohe Entropie, kein Salt nötig).</summary>
public static class RecoveryCodes
{
    public const int Count = 10;

    public static IReadOnlyList<string> Create()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return Enumerable.Range(0, Count).Select(_ =>
        {
            var chars = Enumerable.Range(0, 10).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
            return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
        }).ToList();
    }

    public static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())));
}

/// <summary>
/// Verschlüsselt die TOTP-Geheimnisse (SecretBox) und stellt die kurzlebigen Anmelde-Challenges aus. Eine Challenge
/// hat eine eigene Audience und ist deshalb kein Zugriffstoken.
/// </summary>
public sealed class TwoFactorService(SecretBox secrets, IOptions<PlatformOptions> options, PlatformProduct product, TimeProvider time)
{
    public const string ChallengeAudienceSuffix = ":2fa";
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private const string SetupClaim = "kz_2fa_setup";

    /// <summary>Name in der Authenticator-App.</summary>
    public string Issuer => product.Name;

    public string Protect(string secret) => secrets.Protect(secret);

    public string Unprotect(string protectedSecret) => secrets.Unprotect(protectedSecret);

    public TwoFactorChallenge IssueChallenge(Guid userId, bool setupRequired)
    {
        var jwt = options.Value.Jwt;
        var now = time.GetUtcNow();
        var expires = now + ChallengeLifetime;
        var claims = new Dictionary<string, object> { [PlatformClaims.UserId] = userId.ToString(), [SetupClaim] = setupRequired };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience + ChallengeAudienceSuffix,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(Tokens.KeyOf(jwt), SecurityAlgorithms.HmacSha256),
        });
        return new TwoFactorChallenge(token, expires, setupRequired);
    }

    /// <summary>Benutzer aus einer gültigen Challenge, sonst null.</summary>
    public async Task<Guid?> ReadChallengeAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var jwt = options.Value.Jwt;
        var parameters = Tokens.ValidationOf(jwt);
        parameters.ValidAudience = jwt.Audience + ChallengeAudienceSuffix;
        parameters.LifetimeValidator = (nb, exp, _, _) => exp is { } e && e > time.GetUtcNow().UtcDateTime && (nb is null || nb <= time.GetUtcNow().UtcDateTime.AddMinutes(1));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        if (!result.IsValid) return null;
        return Guid.TryParse(result.ClaimsIdentity.FindFirst(PlatformClaims.UserId)?.Value, out var id) ? id : null;
    }
}
