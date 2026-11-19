using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PlayerInfo.Api.Configuration;

namespace PlayerInfo.Api.Security;

public sealed record ExternalSignInResult(bool Succeeded, string? Email, string? Error)
{
    public static ExternalSignInResult Fail(string error) => new(false, null, error);
}

public interface IGoogleIdTokenValidator
{
    Task<ExternalSignInResult> ValidateAsync(string idToken, CancellationToken ct = default);
}

/// <summary>
/// Validates a Google ID token (the "credential" from Google Identity Services in the browser):
/// signed by Google, issued to <see cref="AuthOptions.GoogleClientId"/>, recent, with a verified email
/// that is in <see cref="AuthOptions.AllowedGoogleEmails"/>. Signing keys come from Google's OpenID
/// Connect metadata (cached and refreshed by the configuration manager).
/// </summary>
public sealed class GoogleIdTokenValidator(
    IOptions<AuthOptions> options,
    IConfigurationManager<OpenIdConnectConfiguration> metadata,
    TimeProvider timeProvider,
    ILogger<GoogleIdTokenValidator> logger) : IGoogleIdTokenValidator
{
    /// <summary>The ID token is exchanged right after the Google button returns it, so it must be recent.</summary>
    public static readonly TimeSpan MaxTokenAge = TimeSpan.FromMinutes(10);

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<ExternalSignInResult> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        var cfg = options.Value;
        if (!cfg.GoogleLoginEnabled)
        {
            return ExternalSignInResult.Fail("Đăng nhập Google chưa được cấu hình.");
        }

        var result = await ValidateSignatureAndClaimsAsync(idToken, cfg, ct);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Google rotated its signing keys: refresh the metadata once and retry.
            metadata.RequestRefresh();
            result = await ValidateSignatureAndClaimsAsync(idToken, cfg, ct);
        }

        if (!result.IsValid)
        {
            logger.LogWarning(result.Exception, "Rejected Google ID token");
            return ExternalSignInResult.Fail("Token Google không hợp lệ hoặc đã hết hạn.");
        }

        var token = (JsonWebToken)result.SecurityToken;

        var issuedAt = token.IssuedAt == DateTime.MinValue ? token.ValidFrom : token.IssuedAt;
        if (timeProvider.GetUtcNow().UtcDateTime - issuedAt > MaxTokenAge)
        {
            return ExternalSignInResult.Fail("Token Google đã quá cũ, vui lòng đăng nhập lại.");
        }

        if (!token.TryGetClaim("email_verified", out var verified) ||
            !string.Equals(verified.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignInResult.Fail("Email Google chưa được xác minh.");
        }

        var email = token.TryGetClaim("email", out var emailClaim) ? emailClaim.Value.Trim().ToLowerInvariant() : null;
        if (string.IsNullOrEmpty(email))
        {
            return ExternalSignInResult.Fail("Token Google không có email.");
        }

        if (!cfg.GetAllowedGoogleEmails().Contains(email))
        {
            logger.LogWarning("Google account {Email} is not in Auth:AllowedGoogleEmails", email);
            return ExternalSignInResult.Fail($"Tài khoản {email} không có quyền truy cập.");
        }

        return new ExternalSignInResult(true, email, null);
    }

    private async Task<TokenValidationResult> ValidateSignatureAndClaimsAsync(string idToken, AuthOptions cfg, CancellationToken ct)
    {
        OpenIdConnectConfiguration oidc;
        try
        {
            oidc = await metadata.GetConfigurationAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not load Google OpenID Connect metadata");
            return new TokenValidationResult { IsValid = false, Exception = ex };
        }

        return await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuers = AuthOptions.GoogleIssuers,
            ValidAudience = cfg.GoogleClientId.Trim(),
            IssuerSigningKeys = oidc.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        });
    }
}
