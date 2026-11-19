using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Security;
using PlayerInfo.UnitTests.Helpers;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PlayerInfo.UnitTests;

public sealed class GoogleIdTokenValidatorTests
{
    private const string ClientId = "1234567890-abcdefghijklmnop.apps.googleusercontent.com";
    private const string AllowedEmail = "owner@gmail.com";

    private static readonly RsaSecurityKey GoogleKey = new(RSA.Create(2048)) { KeyId = "google-key-1" };
    private static readonly RsaSecurityKey AttackerKey = new(RSA.Create(2048)) { KeyId = "google-key-1" };

    private static GoogleIdTokenValidator CreateValidator(string allowed = AllowedEmail)
    {
        var oidc = new OpenIdConnectConfiguration();
        oidc.SigningKeys.Add(GoogleKey);

        return new GoogleIdTokenValidator(
            MsOptions.Create(new AuthOptions { GoogleClientId = ClientId, AllowedGoogleEmails = allowed }),
            new StaticConfigurationManager<OpenIdConnectConfiguration>(oidc),
            TimeProvider.System,
            NullLogger<GoogleIdTokenValidator>.Instance);
    }

    private static string CreateIdToken(
        string? email = AllowedEmail,
        bool? emailVerified = true,
        string audience = ClientId,
        string issuer = "https://accounts.google.com",
        DateTime? issuedAt = null,
        DateTime? expires = null,
        RsaSecurityKey? key = null)
    {
        var iat = issuedAt ?? DateTime.UtcNow.AddMinutes(-1);
        var claims = new Dictionary<string, object> { ["sub"] = "109876543210" };
        if (email is not null) claims["email"] = email;
        if (emailVerified is not null) claims["email_verified"] = emailVerified.Value;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = iat,
            NotBefore = iat,
            Expires = expires ?? iat.AddHours(1),
            SigningCredentials = new SigningCredentials(key ?? GoogleKey, SecurityAlgorithms.RsaSha256)
        });
    }

    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("accounts.google.com")]
    public async Task AcceptsValidToken_FromEitherGoogleIssuer(string issuer)
    {
        var result = await CreateValidator().ValidateAsync(CreateIdToken(issuer: issuer));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(AllowedEmail, result.Email);
    }

    [Fact]
    public async Task AllowListIsCaseInsensitive_AndSupportsSeveralEmails()
    {
        var result = await CreateValidator("someone@gmail.com; Owner@Gmail.com").ValidateAsync(CreateIdToken(email: "OWNER@gmail.com"));
        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task RejectsEmailNotInAllowList()
        => Assert.False((await CreateValidator().ValidateAsync(CreateIdToken(email: "stranger@gmail.com"))).Succeeded);

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task RejectsUnverifiedEmail(bool? verified)
        => Assert.False((await CreateValidator().ValidateAsync(CreateIdToken(emailVerified: verified))).Succeeded);

    [Fact]
    public async Task RejectsTokenIssuedToAnotherApp()
        => Assert.False((await CreateValidator().ValidateAsync(CreateIdToken(audience: "999-other.apps.googleusercontent.com"))).Succeeded);

    [Fact]
    public async Task RejectsNonGoogleIssuer()
        => Assert.False((await CreateValidator().ValidateAsync(CreateIdToken(issuer: "https://evil.example.com"))).Succeeded);

    [Fact]
    public async Task RejectsTokenNotSignedByGoogle()
        => Assert.False((await CreateValidator().ValidateAsync(CreateIdToken(key: AttackerKey))).Succeeded);

    [Fact]
    public async Task RejectsExpiredToken()
    {
        var iat = DateTime.UtcNow.AddHours(-2);
        Assert.False((await CreateValidator().ValidateAsync(CreateIdToken(issuedAt: iat, expires: iat.AddHours(1)))).Succeeded);
    }

    [Fact]
    public async Task RejectsTokenOlderThanMaxAge_EvenIfNotExpired()
    {
        var iat = DateTime.UtcNow - GoogleIdTokenValidator.MaxTokenAge - TimeSpan.FromMinutes(5);
        Assert.False((await CreateValidator().ValidateAsync(CreateIdToken(issuedAt: iat, expires: iat.AddHours(1)))).Succeeded);
    }

    [Fact]
    public async Task RejectsGarbage()
        => Assert.False((await CreateValidator().ValidateAsync("not-a-jwt")).Succeeded);

    [Fact]
    public void AuthOptions_GoogleOnly_IsValid_AndPasswordDisabled()
    {
        var options = new AuthOptions
        {
            GoogleClientId = ClientId,
            AllowedGoogleEmails = AllowedEmail,
            JwtSigningKey = new string('k', 32)
        };

        Assert.Null(options.GetValidationError());
        Assert.True(options.GoogleLoginEnabled);
        Assert.False(options.PasswordLoginEnabled);
    }

    [Theory]
    [InlineData("not-a-google-client-id", AllowedEmail, "apps.googleusercontent.com")]
    [InlineData(ClientId, "", "AllowedGoogleEmails")]
    public void AuthOptions_RejectsIncompleteGoogleSettings(string clientId, string emails, string expected)
    {
        var error = new AuthOptions
        {
            AdminPasswordHash = "hash",
            GoogleClientId = clientId,
            AllowedGoogleEmails = emails,
            JwtSigningKey = new string('k', 32)
        }.GetValidationError();

        Assert.Contains(expected, error);
    }
}

/// <summary>API configured for Google sign-in, with the ID token validator replaced by a fake.</summary>
public sealed class GoogleApiFactory : ApiFactory
{
    public const string ClientId = "1234567890-abcdefghijklmnop.apps.googleusercontent.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:GoogleClientId", ClientId);
        builder.UseSetting("Auth:AllowedGoogleEmails", "owner@gmail.com");
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IGoogleIdTokenValidator, FakeGoogleValidator>()));
    }

    private sealed class FakeGoogleValidator : IGoogleIdTokenValidator
    {
        public Task<ExternalSignInResult> ValidateAsync(string idToken, CancellationToken ct = default)
            => Task.FromResult(idToken == "good-credential"
                ? new ExternalSignInResult(true, "owner@gmail.com", null)
                : ExternalSignInResult.Fail("rejected"));
    }
}

public sealed class GoogleSignInEndpointTests(GoogleApiFactory factory) : IClassFixture<GoogleApiFactory>
{
    [Fact]
    public async Task Config_ExposesGoogleClientId_AndPasswordFallback()
    {
        var config = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/auth/config");

        Assert.True(config.GetProperty("passwordLogin").GetBoolean());
        Assert.Equal(GoogleApiFactory.ClientId, config.GetProperty("google").GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task GoogleLogin_WithValidCredential_IssuesWorkingAppToken()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/google", new { credential = "good-credential" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("owner@gmail.com", body.GetProperty("username").GetString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("owner@gmail.com", me.GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/watchlist/teams")).StatusCode);
    }

    [Theory]
    [InlineData("bad-credential", HttpStatusCode.Unauthorized)]
    [InlineData("", HttpStatusCode.BadRequest)]
    public async Task GoogleLogin_RejectsInvalidCredential(string credential, HttpStatusCode expected)
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/google", new { credential });
        Assert.Equal(expected, res.StatusCode);
    }
}

public sealed class GoogleNotConfiguredTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Config_HasNoGoogle_AndEndpointIsDisabled()
    {
        var client = factory.CreateClient();
        var config = await client.GetFromJsonAsync<JsonElement>("/api/auth/config");
        Assert.Equal(JsonValueKind.Null, config.GetProperty("google").ValueKind);

        var res = await client.PostAsJsonAsync("/api/auth/google", new { credential = "anything" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
