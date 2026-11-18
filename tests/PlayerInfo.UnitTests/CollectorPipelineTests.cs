using System.Net;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class CollectorPipelineTests
{
    private static async Task<Team> SeedArsenalAsync(PipelineHarness h)
    {
        var team = new Team { ExternalApiId = 42, Name = "Arsenal", IsTracked = true };
        h.Db.Teams.Add(team);
        await h.Db.SaveChangesAsync();
        h.Db.WatchlistAliases.Add(new WatchlistAlias { EntityType = "Team", EntityId = team.Id, Keyword = "Arsenal", Language = "en" });
        await h.Db.SaveChangesAsync();
        return team;
    }

    private static string GeminiFor(long articleId, string matched = "\"Arsenal\"", bool relevant = true)
        => TestDbFactory.GeminiResponse(
            $$"""[{"articleId":{{articleId}},"isRelevant":{{(relevant ? "true" : "false")}},"category":"matchperformance","summaryVi":"Arsenal thắng trận derby.","matchedEntities":[{{matched}}]}]""");

    [Fact]
    public async Task RunAsync_NewsMode_DeduplicatesUrlsAndLinksMatchedTeams()
    {
        await using var h = new PipelineHarness();
        var team = await SeedArsenalAsync(h);

        // Pre-insert one article to verify deduplication against existing DB rows
        h.Db.NewsArticles.Add(new NewsArticle
        {
            UrlHash = Deduplicator.ComputeUrlHash("https://bbc.com/already-exists"),
            Url = "https://bbc.com/already-exists",
            Title = "Old Article",
            IsAiProcessed = true,
            IsNotified = true
        });
        await h.Db.SaveChangesAsync();

        h.Rss = _ => TestDbFactory.XmlResponse(TestDbFactory.Rss(
            ("Old Article Duplicate", "https://bbc.com/already-exists?utm_source=rss"),
            ("Arsenal win derby match", "https://bbc.com/arsenal-derby")));
        // "Arsenal" and "Arsenal FC" both resolve to the same team: must not add the link twice.
        h.Gemini = _ => TestDbFactory.JsonResponse(GeminiFor(2, "\"Arsenal\",\"Arsenal FC\""));

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        var allArticles = await read.NewsArticles.Include(a => a.Entities).ToListAsync();
        Assert.Equal(2, allArticles.Count);

        var newArticle = allArticles.Single(a => a.Url == "https://bbc.com/arsenal-derby");
        Assert.True(newArticle.IsAiProcessed);
        Assert.Equal("MatchPerformance", newArticle.Category); // normalized casing
        Assert.Equal("Arsenal thắng trận derby.", newArticle.SummaryVi);
        Assert.Equal(team.Id, Assert.Single(newArticle.Entities).EntityId);
    }

    [Fact]
    public async Task RunAsync_DeduplicatesSameStoryAcrossGoogleNewsAndPublisherFeed()
    {
        await using var h = new PipelineHarness();
        await SeedArsenalAsync(h);
        h.Collector.SportsRssFeeds = [new("BBC Sport", "https://feeds.bbci.co.uk/sport/football/rss.xml", "en")];

        h.Rss = req => TestDbFactory.XmlResponse(req.RequestUri!.Host == "news.google.com"
            ? """
              <?xml version="1.0" encoding="UTF-8"?>
              <rss version="2.0"><channel><title>Google News</title>
                <item><title>Arsenal beat Spurs in the North London derby - BBC Sport</title>
                  <link>https://news.google.com/rss/articles/CBMiXyz</link><source url="https://bbc.co.uk">BBC Sport</source></item>
              </channel></rss>
              """
            : TestDbFactory.Rss(("Arsenal beat Spurs in the North London derby", "https://www.bbc.co.uk/sport/football/123")));

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        Assert.Single(read.NewsArticles);
    }

    [Fact]
    public async Task RunAsync_GeminiFailure_LeavesArticlesPendingAndDoesNotNotify()
    {
        await using var h = new PipelineHarness();
        await SeedArsenalAsync(h);
        h.TelegramOptions.BotToken = "token";
        h.TelegramOptions.ChatId = "chat";
        h.Rss = _ => TestDbFactory.XmlResponse(TestDbFactory.Rss(("Arsenal win derby match", "https://bbc.com/arsenal-derby")));
        h.Gemini = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        var article = await read.NewsArticles.SingleAsync();
        Assert.False(article.IsAiProcessed);
        Assert.False(article.IsNotified);
        Assert.Empty(h.TelegramHandler!.Requests);
    }

    [Fact]
    public async Task RunAsync_WithoutGeminiKey_MarksArticlesProcessedUnfiltered()
    {
        await using var h = new PipelineHarness();
        await SeedArsenalAsync(h);
        h.ApiKeys.Gemini = string.Empty;
        h.Rss = _ => TestDbFactory.XmlResponse(TestDbFactory.Rss(("Arsenal win derby match", "https://bbc.com/arsenal-derby")));

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        var article = await read.NewsArticles.SingleAsync();
        Assert.True(article.IsAiProcessed);
        Assert.True(article.IsRelevant);
        Assert.Null(article.SummaryVi);
    }

    [Fact]
    public async Task RunAsync_GivesUpOnArticlesPendingTooLong()
    {
        await using var h = new PipelineHarness();
        h.Gemini = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        h.Db.NewsArticles.Add(new NewsArticle
        {
            UrlHash = "stale",
            Url = "https://example.com/stale",
            Title = "Stale",
            FetchedAtUtc = DateTime.UtcNow - CollectorPipeline.PendingAiMaxAge - TimeSpan.FromHours(1)
        });
        await h.Db.SaveChangesAsync();

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        var article = await read.NewsArticles.SingleAsync();
        Assert.True(article.IsAiProcessed);
        Assert.True(article.IsNotified); // never pushed to Telegram
    }

    [Fact]
    public async Task RunAsync_Telegram_MarksOnlyDeliveredArticlesAndSkipsUnprocessed()
    {
        await using var h = new PipelineHarness();
        h.ApiKeys.Gemini = string.Empty;
        h.TelegramOptions.BotToken = "token";
        h.TelegramOptions.ChatId = "chat";
        h.Telegram = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);

        h.Db.NewsArticles.Add(new NewsArticle { UrlHash = "a", Url = "https://e.com/a", Title = "A", IsAiProcessed = true, PublishedAtUtc = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();

        // Failed send: nothing marked notified.
        await h.Build().RunAsync(CollectorMode.News);
        await using (var read = h.NewContext())
        {
            Assert.False((await read.NewsArticles.SingleAsync()).IsNotified);
        }

        // Successful send: marked notified.
        h.Telegram = _ => TestDbFactory.JsonResponse("{\"ok\":true}");
        await h.Build().RunAsync(CollectorMode.News);
        await using (var read = h.NewContext())
        {
            Assert.True((await read.NewsArticles.SingleAsync()).IsNotified);
        }
    }

    [Fact]
    public async Task RunAsync_FailingStep_DoesNotStopLaterSteps()
    {
        await using var h = new PipelineHarness();
        await SeedArsenalAsync(h);
        h.ApiKeys.ApiFootball = "key";
        h.ApiKeys.Gemini = string.Empty;
        h.Collector.TrackedLeagueIds = [39];
        // A transfers payload with "teams": null throws inside the SyncTransfers step.
        h.ApiFootball = req => req.RequestUri!.AbsolutePath.Contains("transfers")
            ? TestDbFactory.JsonResponse("""{"response":[{"player":{"id":1,"name":"X"},"transfers":[{"date":"2099-01-01","type":"Loan","teams":null}]}]}""")
            : TestDbFactory.JsonResponse("{\"response\":[]}");
        h.Rss = _ => TestDbFactory.XmlResponse(TestDbFactory.Rss(("Arsenal win derby match", "https://bbc.com/arsenal-derby")));

        await h.Build().RunAsync(CollectorMode.Full);

        await using var read = h.NewContext();
        Assert.Single(read.NewsArticles); // news steps still ran after SyncTransfers failed
    }

    [Fact]
    public async Task RunAsync_Transfers_DeduplicatesTransferBetweenTwoTrackedTeams()
    {
        await using var h = new PipelineHarness();
        h.ApiKeys.ApiFootball = "key";
        h.Db.Teams.AddRange(
            new Team { ExternalApiId = 42, Name = "Arsenal" },
            new Team { ExternalApiId = 47, Name = "Tottenham" });
        await h.Db.SaveChangesAsync();

        var date = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        const string payload = """
            {"response":[{"player":{"id":7,"name":"Player Seven"},"transfers":[
              {"date":"DATE","type":"€ 50M","teams":{"in":{"id":42,"name":"Arsenal"},"out":{"id":47,"name":"Tottenham"}}}
            ]}]}
            """;
        h.ApiFootball = _ => TestDbFactory.JsonResponse(payload.Replace("DATE", date));

        await h.Build().RunAsync(CollectorMode.TransfersNews);

        await using var read = h.NewContext();
        var transfer = await read.PlayerTransfers.SingleAsync();
        Assert.Equal(DateTimeKind.Utc, transfer.TransferDateUtc.Kind);
        Assert.Equal(2, await read.ApiQuotaLogs.Where(x => x.Provider == "ApiFootball").Select(x => x.RequestCount).SingleAsync());
    }

    [Fact]
    public void ParseTransferDate_UsesInvariantCultureAndUtc()
    {
        var parsed = CollectorPipeline.ParseTransferDate("2026-07-01");

        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
        Assert.Null(CollectorPipeline.ParseTransferDate("not a date"));
    }

    [Theory]
    [InlineData("transfer", "Transfer")]
    [InlineData("Gossip", "Other")]
    [InlineData(null, "Other")]
    public void NormalizeCategory_MapsToKnownCategories(string? input, string expected)
        => Assert.Equal(expected, CollectorPipeline.NormalizeCategory(input));
}
