using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class CollectorPipelineTests
{
    [Fact]
    public async Task RunAsync_NewsMode_DeduplicatesUrlsAndLinksMatchedTeams()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var team = new Team { ExternalApiId = 42, Name = "Arsenal", IsTracked = true };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        db.WatchlistAliases.Add(new WatchlistAlias { EntityType = "Team", EntityId = team.Id, Keyword = "Arsenal", Language = "en" });
        // Pre-insert one article to verify deduplication against existing DB rows
        db.NewsArticles.Add(new NewsArticle
        {
            UrlHash = Deduplicator.ComputeUrlHash("https://bbc.com/already-exists"),
            Url = "https://bbc.com/already-exists",
            Title = "Old Article",
            IsAiProcessed = true,
            IsNotified = true
        });
        await db.SaveChangesAsync();

        const string rssXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>Google News</title>
                <item>
                  <title>Old Article Duplicate</title>
                  <link>https://bbc.com/already-exists?utm_source=rss</link>
                </item>
                <item>
                  <title>Arsenal win derby match</title>
                  <link>https://bbc.com/arsenal-derby</link>
                </item>
              </channel>
            </rss>
            """;

        const string geminiJson = """
            {
              "candidates": [{
                "content": {
                  "parts": [{
                    "text": "[{\"articleId\":2,\"isRelevant\":true,\"category\":\"MatchPerformance\",\"summaryVi\":\"Arsenal thắng trận derby.\",\"matchedEntities\":[\"Arsenal\"]}]"
                  }]
                }
              }]
            }
            """;

        var rssClient = new RssNewsClient(
            new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.XmlResponse(rssXml))),
            NullLogger<RssNewsClient>.Instance);

        var quota = new QuotaManager(db, NullLogger<QuotaManager>.Instance);
        var collectorOpts = Options.Create(new CollectorOptions { SportsRssFeeds = [] });
        var apiKeysOpts = Options.Create(new ApiKeysOptions { Gemini = "key" });

        var apiFootball = new ApiFootballClient(new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse("{}"))), quota, apiKeysOpts, collectorOpts, NullLogger<ApiFootballClient>.Instance);
        var newsData = new NewsDataClient(new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse("{}"))), quota, apiKeysOpts, collectorOpts, NullLogger<NewsDataClient>.Instance);
        var gemini = new GeminiEnricherClient(
            new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(geminiJson))) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") },
            apiKeysOpts, collectorOpts, NullLogger<GeminiEnricherClient>.Instance);
        var telegram = new TelegramNotifier(
            new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse("{}"))),
            Options.Create(new TelegramOptions()),
            NullLogger<TelegramNotifier>.Instance);

        var pipeline = new CollectorPipeline(db, apiFootball, rssClient, newsData, gemini, telegram, collectorOpts, NullLogger<CollectorPipeline>.Instance);

        await pipeline.RunAsync(CollectorMode.News);

        var allArticles = await db.NewsArticles.Include(a => a.Entities).ToListAsync();
        Assert.Equal(2, allArticles.Count);

        var newArticle = allArticles.Single(a => a.Url == "https://bbc.com/arsenal-derby");
        Assert.True(newArticle.IsAiProcessed);
        Assert.Equal("MatchPerformance", newArticle.Category);
        Assert.Equal("Arsenal thắng trận derby.", newArticle.SummaryVi);
        Assert.Single(newArticle.Entities);
        Assert.Equal(team.Id, newArticle.Entities.First().EntityId);
    }
}
