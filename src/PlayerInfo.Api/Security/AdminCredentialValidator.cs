using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;

namespace PlayerInfo.Api.Security;

/// <summary>
/// Verifies the single admin account configured in <see cref="AuthOptions"/>.
/// </summary>
public sealed class AdminCredentialValidator(IOptions<AuthOptions> options, ILogger<AdminCredentialValidator> logger)
{
    private sealed class AdminAccount;

    private static readonly PasswordHasher<AdminAccount> Hasher = new();
    private static readonly AdminAccount Account = new();

    public static string HashPassword(string password) => Hasher.HashPassword(Account, password);

    public bool Validate(string? username, string? password)
    {
        var cfg = options.Value;
        if (!cfg.PasswordLoginEnabled || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var usernameMatches = FixedTimeEquals(username.Trim().ToLowerInvariant(), cfg.AdminUsername.Trim().ToLowerInvariant());

        // Always verify the password, even for a wrong username, so response time does not reveal valid usernames.
        PasswordVerificationResult result;
        try
        {
            result = Hasher.VerifyHashedPassword(Account, cfg.AdminPasswordHash, password);
        }
        catch (FormatException)
        {
            logger.LogError("Auth:AdminPasswordHash is not a valid password hash; regenerate it with the hash-password command");
            return false;
        }

        return usernameMatches && result != PasswordVerificationResult.Failed;
    }

    public static bool FixedTimeEquals(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        // Hash both sides so neither the content nor the length of the expected value leaks through timing.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
