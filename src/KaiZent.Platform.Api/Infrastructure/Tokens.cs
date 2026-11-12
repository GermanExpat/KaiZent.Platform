using System.Globalization;
using System.Text;
using KaiZent.Platform.Contracts.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KaiZent.Platform.Api.Infrastructure;

/// <summary>Stellt die Zugriffstoken (JWT, HMAC-SHA256) aus.</summary>
public sealed class Tokens(IOptions<PlatformOptions> options, TimeProvider time)
{
    public const int MinKeyLength = 32;

    public static SymmetricSecurityKey KeyOf(JwtOptions jwt)
    {
        if (jwt.SigningKey.Length < MinKeyLength)
            throw new InvalidOperationException($"Platform:Jwt:SigningKey fehlt oder ist kürzer als {MinKeyLength} Zeichen.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey));
    }

    public static TokenValidationParameters ValidationOf(JwtOptions jwt) => new()
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = KeyOf(jwt),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = PlatformClaims.Name,
        RoleClaimType = PlatformClaims.Role,
    };

    /// <summary>Token für das Konto; <paramref name="sessionVersion"/> ist der Sitzungsstand (catalog.app_user.session_version).</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, string email, string displayName, string role, Guid? tenantId, int sessionVersion)
    {
        var jwt = options.Value.Jwt;
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(jwt.LifetimeMinutes);
        var claims = new Dictionary<string, object>
        {
            [PlatformClaims.UserId] = userId.ToString(),
            [PlatformClaims.Email] = email,
            [PlatformClaims.Name] = displayName,
            [PlatformClaims.Role] = role,
            [PlatformClaims.SessionVersion] = sessionVersion.ToString(CultureInfo.InvariantCulture),
            [PlatformClaims.TokenId] = Guid.NewGuid().ToString("N"),
        };
        if (tenantId is { } tenant) claims[PlatformClaims.TenantId] = tenant.ToString();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(KeyOf(jwt), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}

/// <summary>Passwort-Hashes mit dem Verfahren von ASP.NET Core Identity (PBKDF2, Salt, Iterationen im Hash).</summary>
public static class Passwords
{
    private sealed class Account;
    private static readonly Microsoft.AspNetCore.Identity.PasswordHasher<Account> Hasher = new();
    private static readonly Account Any = new();

    /// <summary>Hash für unbekannte Benutzer, damit die Antwortzeit nicht verrät, ob es ein Konto gibt.</summary>
    private static readonly string Dummy = Hash("kein-konto-" + Guid.NewGuid());

    public static string Hash(string password) => Hasher.HashPassword(Any, password);

    public static bool Verify(string? hash, string password) =>
        Hasher.VerifyHashedPassword(Any, hash ?? Dummy, password) != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed && hash is not null;
}
