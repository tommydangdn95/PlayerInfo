namespace PlayerInfo.Api.Configuration;

/// <summary>
/// Admin sign-in + JWT settings. Two sign-in methods, either or both may be enabled:
/// <list type="bullet">
/// <item>Google account (Auth__GoogleClientId + Auth__AllowedGoogleEmails), the primary method.</item>
/// <item>Username/password (Auth__AdminUsername + Auth__AdminPasswordHash), the fallback.</item>
/// </list>
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const string Issuer = "PlayerInfo";
    public const string Audience = "PlayerInfo.Admin";
    public const int MinSigningKeyLength = 32;

    /// <summary>Google signs ID tokens with either issuer form.</summary>
    public static readonly string[] GoogleIssuers = ["https://accounts.google.com", "accounts.google.com"];
    public const string GoogleMetadataAddress = "https://accounts.google.com/.well-known/openid-configuration";
    public const string GoogleClientIdSuffix = ".apps.googleusercontent.com";

    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// ASP.NET Core Identity password hash (PBKDF2). Empty disables password sign-in. Generate with:
    /// <c>dotnet run --project src/PlayerInfo.Api -- hash-password "your-password"</c>
    /// </summary>
    public string AdminPasswordHash { get; set; } = string.Empty;

    /// <summary>OAuth 2.0 Web client ID from Google Cloud Console (ends with .apps.googleusercontent.com). Empty disables Google sign-in.</summary>
    public string GoogleClientId { get; set; } = string.Empty;

    /// <summary>Google account emails allowed to sign in, separated by comma or semicolon.</summary>
    public string AllowedGoogleEmails { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 key for signing tokens, at least 32 characters.</summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    public int TokenLifetimeHours { get; set; } = 12;

    /// <summary>Sign-in attempts allowed per client IP per minute (password and Google combined).</summary>
    public int LoginAttemptsPerMinute { get; set; } = 5;

    /// <summary>Set when Development defaults (password "admin") are in use.</summary>
    public bool UsingDevelopmentDefaults { get; set; }

    public bool PasswordLoginEnabled =>
        !string.IsNullOrWhiteSpace(AdminUsername) && !string.IsNullOrWhiteSpace(AdminPasswordHash);

    public bool GoogleLoginEnabled =>
        !string.IsNullOrWhiteSpace(GoogleClientId) && GetAllowedGoogleEmails().Count > 0;

    public IReadOnlySet<string> GetAllowedGoogleEmails() => AllowedGoogleEmails
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(e => e.ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? GetValidationError()
    {
        if (!PasswordLoginEnabled && !GoogleLoginEnabled)
        {
            return "No admin sign-in method configured: set Auth:GoogleClientId + Auth:AllowedGoogleEmails, " +
                   "and/or Auth:AdminPasswordHash (generate it with: dotnet run --project src/PlayerInfo.Api -- hash-password \"<password>\").";
        }

        if (!string.IsNullOrWhiteSpace(GoogleClientId))
        {
            if (!GoogleClientId.Trim().EndsWith(GoogleClientIdSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return $"Auth:GoogleClientId must be the OAuth client ID ending with {GoogleClientIdSuffix}.";
            }

            if (GetAllowedGoogleEmails().Count == 0)
            {
                return "Auth:AllowedGoogleEmails is required when Auth:GoogleClientId is set.";
            }
        }

        if (string.IsNullOrEmpty(JwtSigningKey) || JwtSigningKey.Length < MinSigningKeyLength)
        {
            return $"Auth:JwtSigningKey must be at least {MinSigningKeyLength} characters.";
        }

        if (TokenLifetimeHours is < 1 or > 24 * 7)
        {
            return "Auth:TokenLifetimeHours must be between 1 and 168.";
        }

        return null;
    }
}
