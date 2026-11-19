using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PlayerInfo.Api.Configuration;

namespace PlayerInfo.Api.Security;

public static class Roles
{
    public const string Admin = "admin";
    public const string Cron = "cron";
}

public static class Policies
{
    /// <summary>Admin JWT or the cron-job.org secret header.</summary>
    public const string AdminOrCron = "AdminOrCron";

    public const string LoginRateLimit = "login";
}

/// <summary>
/// Issues and describes validation of admin access tokens (HS256).
/// </summary>
public sealed class JwtTokenService(IOptions<AuthOptions> options, TimeProvider timeProvider)
{
    public const string NameClaim = "sub";
    public const string RoleClaim = "role";

    public (string Token, DateTime ExpiresAtUtc) CreateToken(string username)
    {
        var cfg = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.AddHours(cfg.TokenLifetimeHours);

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AuthOptions.Issuer,
            Audience = AuthOptions.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(NameClaim, username),
                new Claim(RoleClaim, Roles.Admin)
            ]),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(CreateKey(cfg), SecurityAlgorithms.HmacSha256)
        });

        return (token, expires);
    }

    public static TokenValidationParameters CreateValidationParameters(AuthOptions cfg) => new()
    {
        ValidIssuer = AuthOptions.Issuer,
        ValidAudience = AuthOptions.Audience,
        IssuerSigningKey = CreateKey(cfg),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = NameClaim,
        RoleClaimType = RoleClaim
    };

    private static SymmetricSecurityKey CreateKey(AuthOptions cfg) => new(Encoding.UTF8.GetBytes(cfg.JwtSigningKey));
}
