using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PlayerInfo.Api.Configuration;

namespace PlayerInfo.Api.Security;

public static class AuthServiceCollectionExtensions
{
    public const string DevelopmentPassword = "admin";

    /// <summary>
    /// JWT for the admin UI and every API (fallback policy), plus the cron secret scheme for POST /api/jobs/run.
    /// </summary>
    public static IServiceCollection AddPlayerInfoAuth(this IServiceCollection services, IHostEnvironment env)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .PostConfigure(o =>
            {
                if (!env.IsDevelopment())
                {
                    return;
                }

                // Local development convenience only: admin/admin and a per-process random key.
                if (string.IsNullOrWhiteSpace(o.AdminPasswordHash))
                {
                    o.AdminPasswordHash = AdminCredentialValidator.HashPassword(DevelopmentPassword);
                    o.UsingDevelopmentDefaults = true;
                }

                if (string.IsNullOrEmpty(o.JwtSigningKey))
                {
                    o.JwtSigningKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                }
            })
            .Validate(o => o.GetValidationError() is null,
                "Invalid Auth configuration: set Auth__JwtSigningKey (>= 32 characters) and at least one sign-in method " +
                "(Auth__GoogleClientId + Auth__AllowedGoogleEmails, and/or Auth__AdminPasswordHash).")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AdminCredentialValidator>();
        services.AddSingleton<JwtTokenService>();

        // Google sign-in: signing keys from Google's OIDC metadata (fetched lazily, cached, auto-refreshed).
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(_ =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                AuthOptions.GoogleMetadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true }));
        services.AddSingleton<IGoogleIdTokenValidator, GoogleIdTokenValidator>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o => o.MapInboundClaims = false)
            .AddScheme<AuthenticationSchemeOptions, CronSecretAuthenticationHandler>(CronSecretAuthenticationHandler.SchemeName, _ => { });

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
                jwt.TokenValidationParameters = JwtTokenService.CreateValidationParameters(auth.Value));

        var adminPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(Roles.Admin)
            .Build();

        services.AddAuthorization(o =>
        {
            // Every endpoint requires an admin JWT unless it opts out with [AllowAnonymous] or another policy.
            o.DefaultPolicy = adminPolicy;
            o.FallbackPolicy = adminPolicy;
            o.AddPolicy(Policies.AdminOrCron, p => p
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, CronSecretAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(Roles.Admin, Roles.Cron));
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Policies.LoginRateLimit, ctx =>
            {
                var limit = ctx.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginAttemptsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, limit),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });
            });
        });

        // Render terminates TLS at its proxy; take the client IP from the header it appends
        // (ForwardLimit = 1 uses only the last, proxy-added entry, so clients cannot spoof it).
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
        });

        return services;
    }
}
