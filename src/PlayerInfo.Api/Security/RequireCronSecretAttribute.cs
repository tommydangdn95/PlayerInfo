using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;

namespace PlayerInfo.Api.Security;

/// <summary>
/// Rejects requests whose X-Cron-Secret header does not match Cron:SecretKey.
/// When no secret is configured, requests are allowed (local development).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireCronSecretAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Cron-Secret";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<CronOptions>>().Value.SecretKey;

        if (string.IsNullOrEmpty(expected))
        {
            return;
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!SecretEquals(provided, expected))
        {
            context.Result = new UnauthorizedObjectResult(new { error = $"Invalid {HeaderName} header." });
        }
    }

    /// <summary>
    /// Constant-time comparison. Both sides are hashed first so the length of the secret is not leaked either.
    /// </summary>
    public static bool SecretEquals(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
