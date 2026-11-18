using Microsoft.EntityFrameworkCore;
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
builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.SectionName));

var rawConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(rawConnection))
{
    rawConnection = Environment.GetEnvironmentVariable("DATABASE_URL");
}

if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(rawConnection))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseInMemoryDatabase("PlayerInfoDev"));
}
else
{
    var connectionString = ConnectionStringHelper.Normalize(rawConnection);
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null)));
}

builder.Services.AddHttpClient<ApiFootballClient>(c => c.BaseAddress = new Uri("https://v3.api-sports.io/"))
    .AddStandardResilienceHandler();

builder.Services.AddHttpClient<RssNewsClient>()
    .AddStandardResilienceHandler();

builder.Services.AddHttpClient<NewsDataClient>(c => c.BaseAddress = new Uri("https://newsdata.io/api/1/"))
    .AddStandardResilienceHandler();

builder.Services.AddHttpClient<GeminiEnricherClient>(c => c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/"))
    .AddStandardResilienceHandler();

builder.Services.AddHttpClient<TelegramNotifier>()
    .AddStandardResilienceHandler();

builder.Services.AddScoped<QuotaManager>();
builder.Services.AddScoped<CollectorPipeline>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobRunnerService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure database tables exist on Neon without requiring manual migration runs
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapOpenApi();

app.MapControllers();

app.Run();
