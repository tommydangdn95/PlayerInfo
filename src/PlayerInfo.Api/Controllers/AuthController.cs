using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Security;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController(
    IOptions<AuthOptions> authOptions,
    AdminCredentialValidator credentials,
    IGoogleIdTokenValidator google,
    JwtTokenService tokens,
    ILogger<AuthController> logger) : ControllerBase
{
    public sealed record LoginRequest(string? Username, string? Password);

    /// <param name="Credential">The Google ID token returned by Google Identity Services.</param>
    public sealed record GoogleLoginRequest(string? Credential);

    public sealed record LoginResponse(string AccessToken, DateTime ExpiresAtUtc, string Username);

    /// <summary>Which sign-in methods the login page should show. The Google client id is public by design.</summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public IActionResult GetConfig()
    {
        var cfg = authOptions.Value;
        return Ok(new
        {
            passwordLogin = cfg.PasswordLoginEnabled,
            google = cfg.GoogleLoginEnabled ? new { clientId = cfg.GoogleClientId.Trim() } : null
        });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.LoginRateLimit)]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        if (!authOptions.Value.PasswordLoginEnabled)
        {
            return NotFound(new { error = "Đăng nhập bằng mật khẩu đã bị tắt." });
        }

        if (!credentials.Validate(req.Username, req.Password))
        {
            logger.LogWarning("Failed admin password login from {RemoteIp}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Sai tên đăng nhập hoặc mật khẩu." });
        }

        return Ok(Issue(req.Username!.Trim()));
    }

    /// <summary>Exchanges a Google ID token (from the "Sign in with Google" button) for an app access token.</summary>
    [HttpPost("google")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.LoginRateLimit)]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest req, CancellationToken ct)
    {
        if (!authOptions.Value.GoogleLoginEnabled)
        {
            return NotFound(new { error = "Đăng nhập Google chưa được cấu hình." });
        }

        if (string.IsNullOrWhiteSpace(req.Credential))
        {
            return BadRequest(new { error = "Thiếu credential." });
        }

        var result = await google.ValidateAsync(req.Credential, ct);
        if (!result.Succeeded)
        {
            logger.LogWarning("Failed Google login from {RemoteIp}: {Reason}", HttpContext.Connection.RemoteIpAddress, result.Error);
            return Unauthorized(new { error = result.Error });
        }

        return Ok(Issue(result.Email!));
    }

    [HttpGet("me")]
    public IActionResult Me() => Ok(new { username = User.Identity?.Name });

    private LoginResponse Issue(string username)
    {
        var (token, expiresAtUtc) = tokens.CreateToken(username);
        return new LoginResponse(token, expiresAtUtc, username);
    }
}
