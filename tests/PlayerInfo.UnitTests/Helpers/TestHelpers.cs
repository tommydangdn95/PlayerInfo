using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Services;

namespace PlayerInfo.UnitTests.Helpers;

public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(handler(request));
    }
}

/// <summary>IDbContextFactory over a named InMemory database, so several contexts share the same data.</summary>
public sealed class InMemoryDbContextFactory(string databaseName) : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(databaseName)
        .Options;

    public AppDbContext CreateDbContext() => new(_options);
}

public static class TestDbFactory
{
    public static AppDbContext CreateInMemory(string? databaseName = null)
        => new InMemoryDbContextFactory(databaseName ?? Guid.NewGuid().ToString()).CreateDbContext();

    public static QuotaManager CreateQuotaManager(string databaseName)
        => new(new InMemoryDbContextFactory(databaseName), NullLogger<QuotaManager>.Instance);

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage XmlResponse(string xml, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
        };

    public static string GeminiResponse(string innerJson)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = innerJson } } } } }
        });

    public static string Rss(params (string Title, string Link)[] items)
        => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel><title>Feed</title>
            {string.Join("\n", items.Select(i => $"<item><title>{WebUtility.HtmlEncode(i.Title)}</title><link>{WebUtility.HtmlEncode(i.Link)}</link></item>"))}
            </channel></rss>
            """;
}

/// <summary>
/// Builds a CollectorPipeline over an InMemory database with stubbed HTTP for every provider.
/// </summary>
public sealed class PipelineHarness : IAsyncDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    public PipelineHarness()
    {
        Db = TestDbFactory.CreateInMemory(_dbName);
    }

    public AppDbContext Db { get; }

    public Func<HttpRequestMessage, HttpResponseMessage> Rss { get; set; } = _ => TestDbFactory.XmlResponse(TestDbFactory.Rss());
    public Func<HttpRequestMessage, HttpResponseMessage> Gemini { get; set; } = _ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse("""{"articles":[],"stories":[]}"""));
    public Func<HttpRequestMessage, HttpResponseMessage> Telegram { get; set; } = _ => TestDbFactory.JsonResponse("{\"ok\":true}");
    public Func<HttpRequestMessage, HttpResponseMessage> ApiFootball { get; set; } = _ => TestDbFactory.JsonResponse("{\"response\":[]}");
    public Func<HttpRequestMessage, HttpResponseMessage> Espn { get; set; } = _ => TestDbFactory.JsonResponse("{}");

    public ApiKeysOptions ApiKeys { get; } = new() { Gemini = "gemini-key" };
    public TelegramOptions TelegramOptions { get; } = new();
    public CollectorOptions Collector { get; } = new() { SportsRssFeeds = [], Leagues = [] };

    public StubHttpMessageHandler? TelegramHandler { get; private set; }
    public StubHttpMessageHandler? EspnHandler { get; private set; }

    public CollectorPipeline Build()
    {
        var quota = TestDbFactory.CreateQuotaManager(_dbName);
        var keys = Options.Create(ApiKeys);
        var collector = Options.Create(Collector);

        EspnHandler = new StubHttpMessageHandler(Espn);
        var espn = new EspnClient(
            new HttpClient(EspnHandler) { BaseAddress = new Uri(EspnClient.BaseUrl) },
            NullLogger<EspnClient>.Instance);
        var rss = new RssNewsClient(new HttpClient(new StubHttpMessageHandler(Rss)), NullLogger<RssNewsClient>.Instance);
        var apiFootball = new ApiFootballClient(
            new HttpClient(new StubHttpMessageHandler(ApiFootball)) { BaseAddress = new Uri("https://v3.api-sports.io/") },
            quota, keys, collector, NullLogger<ApiFootballClient>.Instance);
        var newsData = new NewsDataClient(
            new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse("{}"))) { BaseAddress = new Uri("https://newsdata.io/api/1/") },
            quota, keys, collector, NullLogger<NewsDataClient>.Instance);
        var gemini = new GeminiEnricherClient(
            new HttpClient(new StubHttpMessageHandler(Gemini)) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") },
            keys, collector, NullLogger<GeminiEnricherClient>.Instance);
        TelegramHandler = new StubHttpMessageHandler(Telegram);
        var telegram = new TelegramNotifier(new HttpClient(TelegramHandler), Options.Create(TelegramOptions), NullLogger<TelegramNotifier>.Instance);

        return new CollectorPipeline(Db, espn, apiFootball, rss, newsData, gemini, telegram, collector, NullLogger<CollectorPipeline>.Instance);
    }

    /// <summary>A fresh context over the same database, to read results without the pipeline's tracked entities.</summary>
    public AppDbContext NewContext() => TestDbFactory.CreateInMemory(_dbName);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
