using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://+:{renderPort}");
}

builder.Services.Configure<ApiKeysOptions>(builder.Configuration.GetSection(ApiKeysOptions.SectionName));
builder.Services.Configure<CronOptions>(builder.Configuration.GetSection(CronOptions.SectionName));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddOptions<CollectorOptions>()
    .Bind(builder.Configuration.GetSection(CollectorOptions.SectionName))
    .PostConfigure(o => o.ApplyDefaults());

// ---------------------------------------------------------------- Database
var rawConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(rawConnection))
{
    rawConnection = Environment.GetEnvironmentVariable("DATABASE_URL");
}

if (string.IsNullOrWhiteSpace(rawConnection))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "No database configured. Set ConnectionStrings__DefaultConnection or DATABASE_URL.");
    }

    builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseInMemoryDatabase("PlayerInfoDev"));
}
else
{
    var connectionString = ConnectionStringHelper.Normalize(rawConnection);
    builder.Services.AddDbContextFactory<AppDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null)));
}

// Scoped context for controllers and the pipeline; QuotaManager creates its own short-lived contexts from the factory.
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// ---------------------------------------------------------------- HTTP clients
// Quota-guarded providers: no automatic retries, because every attempt costs a request
// the QuotaManager would not see.
builder.Services.AddHttpClient<ApiFootballClient>(c => c.BaseAddress = new Uri("https://v3.api-sports.io/"))
    .AddStandardResilienceHandler(DisableRetries);

builder.Services.AddHttpClient<NewsDataClient>(c => c.BaseAddress = new Uri("https://newsdata.io/api/1/"))
    .AddStandardResilienceHandler(DisableRetries);

builder.Services.AddHttpClient<RssNewsClient>()
    .AddStandardResilienceHandler();

// Gemini: a batch of 15 summaries can take well over the default 10s attempt timeout.
builder.Services.AddHttpClient<GeminiEnricherClient>(c =>
    {
        c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
        c.Timeout = TimeSpan.FromSeconds(220);
    })
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(200);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(180); // must be >= 2x attempt timeout
        o.Retry.MaxRetryAttempts = 1;
        o.Retry.Delay = TimeSpan.FromSeconds(5);
    });

// Telegram: never retry the POST, or a timed-out-but-delivered digest gets sent twice.
// An undelivered digest is not marked notified, so the next run sends it instead.
builder.Services.AddHttpClient<TelegramNotifier>()
    .AddStandardResilienceHandler(DisableRetries);

builder.Services.AddScoped<QuotaManager>();
builder.Services.AddScoped<CollectorPipeline>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobRunnerService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (!app.Environment.IsDevelopment() &&
    string.IsNullOrEmpty(app.Services.GetRequiredService<IOptions<CronOptions>>().Value.SecretKey))
{
    app.Logger.LogWarning("Cron:SecretKey is not set: job and watchlist write endpoints are unauthenticated.");
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseInitializer.InitializeAsync(db, app.Logger);
}

app.MapOpenApi();

app.MapControllers();

app.Run();

static void DisableRetries(HttpStandardResilienceOptions o)
    => o.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
