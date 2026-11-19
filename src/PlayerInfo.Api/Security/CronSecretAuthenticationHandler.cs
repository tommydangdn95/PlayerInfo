using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;

namespace PlayerInfo.Api.Security;

/// <summary>
/// Authenticates cron-job.org by the static X-Cron-Secret header. The resulting principal only
/// has the "cron" role, which is accepted solely by the <see cref="Policies.AdminOrCron"/> policy
/// (POST /api/jobs/run). When Cron:SecretKey is not set, the header is never accepted.
/// </summary>
public sealed class CronSecretAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<CronOptions> cronOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "CronSecret";
    public const string HeaderName = "X-Cron-Secret";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var provided) || string.IsNullOrEmpty(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expected = cronOptions.Value.SecretKey;
        if (string.IsNullOrEmpty(expected) || !AdminCredentialValidator.FixedTimeEquals(provided.ToString(), expected))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Invalid {HeaderName} header."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(JwtTokenService.NameClaim, "cron-job"), new Claim(JwtTokenService.RoleClaim, Roles.Cron)],
            SchemeName,
            JwtTokenService.NameClaim,
            JwtTokenService.RoleClaim);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
