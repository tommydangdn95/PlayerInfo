using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Security;

namespace PlayerInfo.UnitTests.Helpers;

/// <summary>
/// Runs the real API (Development, InMemory DB) with known admin credentials and cron secret.
/// The background job runner is removed so queued jobs never call external services.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Username = "admin";
    public const string Password = "Test-Password-123!";
    public const string CronSecret = "cron-secret-for-tests";

    protected virtual int LoginAttemptsPerMinute => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:AdminUsername", Username);
        builder.UseSetting("Auth:AdminPasswordHash", AdminCredentialValidator.HashPassword(Password));
        builder.UseSetting("Auth:JwtSigningKey", "test-signing-key-0123456789-abcdefghijklmnop");
        builder.UseSetting("Auth:LoginAttemptsPerMinute", LoginAttemptsPerMinute.ToString());
        builder.UseSetting("Cron:SecretKey", CronSecret);

        builder.ConfigureTestServices(services =>
        {
            var runner = services.Single(d => d.ImplementationType == typeof(JobRunnerService));
            services.Remove(runner);
        });
    }

    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }

    public sealed record LoginBody(string AccessToken, DateTime ExpiresAtUtc, string Username);
}

public sealed class StrictLoginLimitApiFactory : ApiFactory
{
    protected override int LoginAttemptsPerMinute => 2;
}
