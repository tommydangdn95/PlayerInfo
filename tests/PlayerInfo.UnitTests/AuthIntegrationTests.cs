using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Security;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class AuthIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_IsPublic()
    {
        var res = await factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/news")]
    [InlineData("/api/news/injuries")]
    [InlineData("/api/news/transfers")]
    [InlineData("/api/jobs/quota")]
    [InlineData("/api/watchlist/teams")]
    [InlineData("/api/watchlist/aliases")]
    [InlineData("/api/auth/me")]
    [InlineData("/openapi/v1.json")]
    public async Task ReadEndpoints_RequireLogin(string path)
    {
        var res = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = ApiFactory.Username, password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongUsername_ReturnsUnauthorized()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "root", password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_ThenToken_GrantsAccess()
    {
        var client = await factory.CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/news")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/jobs/quota")).StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(ApiFactory.Username, me.GetProperty("username").GetString());
    }

    [Fact]
    public async Task TamperedToken_IsRejected()
    {
        var admin = await factory.CreateAdminClientAsync();
        var token = admin.DefaultRequestHeaders.Authorization!.Parameter!;
        var tampered = token[..^4] + (token.EndsWith("AAAA") ? "BBBB" : "AAAA");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/news")).StatusCode);
    }

    [Fact]
    public async Task CronSecret_CanOnlyTriggerJobs()
    {
        var cron = factory.CreateClient();
        cron.DefaultRequestHeaders.Add(CronSecretAuthenticationHandler.HeaderName, ApiFactory.CronSecret);

        Assert.Equal(HttpStatusCode.Accepted, (await cron.PostAsync("/api/jobs/run?mode=news", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cron.GetAsync("/api/news")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await cron.PostAsJsonAsync("/api/watchlist/teams", new { externalApiId = 1, name = "X" })).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task JobsRun_WithoutValidSecretOrToken_IsRejected(string? secret)
    {
        var client = factory.CreateClient();
        if (secret is not null)
        {
            client.DefaultRequestHeaders.Add(CronSecretAuthenticationHandler.HeaderName, secret);
        }

        var res = await client.PostAsync("/api/jobs/run?mode=news", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task JobsRun_AcceptsAdminToken()
    {
        var admin = await factory.CreateAdminClientAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/jobs/run?mode=news", null)).StatusCode);
    }

    [Fact]
    public async Task UnknownApiRoute_Returns404_AndUiRoutesArePublic()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode);
        // SPA routes are served without auth (200 when the UI is built into wwwroot, 404 otherwise).
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/teams")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/teams")]
    public async Task UiPages_AreNotCached(string path)
    {
        var res = await factory.CreateClient().GetAsync(path);
        if (res.StatusCode != HttpStatusCode.OK)
        {
            return; // UI not built into wwwroot in this environment
        }

        Assert.True(res.Headers.CacheControl?.NoCache, $"{path} must be served with Cache-Control: no-cache");
    }

    [Fact]
    public async Task TeamManagement_AddUpdateDelete()
    {
        var admin = await factory.CreateAdminClientAsync();
        var externalId = Random.Shared.Next(100_000, 900_000);

        var created = await admin.PostAsJsonAsync("/api/watchlist/teams",
            new { externalApiId = externalId, name = "Test FC", country = "England", logoUrl = "https://media.example/logo.png" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var team = await created.Content.ReadFromJsonAsync<JsonElement>();
        var teamId = team.GetProperty("id").GetInt32();

        var alias = await admin.PostAsJsonAsync("/api/watchlist/aliases",
            new { entityType = "Team", entityId = teamId, keyword = "Test FC", language = "en" });
        Assert.Equal(HttpStatusCode.Created, alias.StatusCode);

        var patched = await admin.PatchAsJsonAsync($"/api/watchlist/teams/{teamId}", new { isTracked = false, leagueExternalId = 39 });
        var patchedBody = await patched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(patchedBody.GetProperty("isTracked").GetBoolean());
        Assert.Equal(39, patchedBody.GetProperty("leagueExternalId").GetInt32());
        Assert.Single(patchedBody.GetProperty("aliases").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/watchlist/teams/{teamId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/watchlist/teams/{teamId}")).StatusCode);

        var aliases = await admin.GetFromJsonAsync<JsonElement>("/api/watchlist/aliases");
        Assert.DoesNotContain(aliases.EnumerateArray(), a => a.GetProperty("entityId").GetInt32() == teamId && a.GetProperty("entityType").GetString() == "Team");
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("arsenal;drop")]
    public async Task SearchTeams_RejectsInvalidQuery(string q)
    {
        var admin = await factory.CreateAdminClientAsync();
        var res = await admin.GetAsync($"/api/watchlist/search-teams?q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Quota_ReportsConfiguredLimits()
    {
        var admin = await factory.CreateAdminClientAsync();
        var quota = await admin.GetFromJsonAsync<JsonElement>("/api/jobs/quota");
        Assert.Equal(90, quota.GetProperty("apiFootballLimit").GetInt32());
        Assert.Equal(180, quota.GetProperty("newsDataLimit").GetInt32());
    }
}

public sealed class LoginRateLimitTests(StrictLoginLimitApiFactory factory) : IClassFixture<StrictLoginLimitApiFactory>
{
    [Fact]
    public async Task Login_IsRateLimited()
    {
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var res = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" });
            statuses.Add(res.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }
}

public sealed class AuthUnitTests
{
    [Fact]
    public void HashPassword_RoundTrips_AndRejectsWrongPassword()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            AdminUsername = "Admin",
            AdminPasswordHash = AdminCredentialValidator.HashPassword("s3cret-pass")
        });
        var validator = new AdminCredentialValidator(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminCredentialValidator>.Instance);

        Assert.True(validator.Validate("admin", "s3cret-pass")); // username is case-insensitive
        Assert.False(validator.Validate("admin", "S3cret-pass"));
        Assert.False(validator.Validate("other", "s3cret-pass"));
        Assert.False(validator.Validate("admin", ""));
    }

    [Fact]
    public void Validate_ReturnsFalse_ForCorruptHash()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions { AdminPasswordHash = "not-a-hash!!" });
        var validator = new AdminCredentialValidator(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminCredentialValidator>.Instance);
        Assert.False(validator.Validate("admin", "anything"));
    }

    [Theory]
    [InlineData("", "0123456789abcdef0123456789abcdef", "AdminPasswordHash")]
    [InlineData("hash", "too-short", "JwtSigningKey")]
    public void AuthOptions_Validation_ReportsMissingSettings(string hash, string key, string expectedField)
    {
        var error = new AuthOptions { AdminPasswordHash = hash, JwtSigningKey = key }.GetValidationError();
        Assert.Contains(expectedField, error);
    }

    [Fact]
    public void AuthOptions_Validation_AcceptsCompleteSettings()
        => Assert.Null(new AuthOptions { AdminPasswordHash = "hash", JwtSigningKey = new string('k', 32) }.GetValidationError());
}
