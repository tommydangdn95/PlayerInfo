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
        var team = new Team { EspnId = "359", LeagueSlug = "eng.1", ApiFootballId = 42, Name = "Arsenal", IsTracked = true };
        h.Db.Teams.Add(team);
        await h.Db.SaveChangesAsync();
        h.Db.WatchlistAliases.Add(new WatchlistAlias { EntityType = "Team", EntityId = team.Id, Keyword = "Arsenal", Language = "en" });
        await h.Db.SaveChangesAsync();
        return team;
    }

    private static string GeminiFor(long articleId, string matched = "\"Arsenal\"", bool relevant = true, bool hot = false)
        => TestDbFactory.GeminiResponse(
            $$"""[{"articleId":{{articleId}},"isRelevant":{{(relevant ? "true" : "false")}},"category":"matchperformance","summaryVi":"Arsenal thắng trận derby.","matchedEntities":[{{matched}}],"isHot":{{(hot ? "true" : "false")}}}]""");

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
        Assert.False(newArticle.IsHot);
    }

    [Fact]
    public async Task RunAsync_News_StoresGeminiHotFlag_OnlyForRelevantArticles()
    {
        await using var h = new PipelineHarness();
        await SeedArsenalAsync(h);
        h.Rss = _ => TestDbFactory.XmlResponse(TestDbFactory.Rss(("Arsenal sack manager", "https://bbc.com/arsenal-sack")));
        h.Gemini = _ => TestDbFactory.JsonResponse(GeminiFor(1, hot: true));

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        Assert.True((await read.NewsArticles.SingleAsync()).IsHot);

        await using var h2 = new PipelineHarness();
        await SeedArsenalAsync(h2);
        h2.Rss = h.Rss;
        h2.Gemini = _ => TestDbFactory.JsonResponse(GeminiFor(1, relevant: false, hot: true));
        await h2.Build().RunAsync(CollectorMode.News);
        await using var read2 = h2.NewContext();
        Assert.False((await read2.NewsArticles.SingleAsync()).IsHot); // spam is never hot
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
        h.Collector.Leagues = [new("eng.1", "Premier League")];
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
            new Team { ApiFootballId = 42, Name = "Arsenal" },
            new Team { ApiFootballId = 47, Name = "Tottenham" });
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

    // ---------------------------------------------------------------- ESPN steps

    private static Func<HttpRequestMessage, HttpResponseMessage> EspnRoutes(string? scoreboard = null, string? roster = null, string? news = null)
        => req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/scoreboard") ? scoreboard
                : path.EndsWith("/roster") ? roster
                : path.EndsWith("/news") ? news
                : null;
            return TestDbFactory.JsonResponse(body ?? "{}");
        };

    [Fact]
    public async Task RunAsync_Full_SyncsFixturesFromEspnScoreboard_AndUpdatesExistingRows()
    {
        await using var h = new PipelineHarness();
        h.ApiKeys.Gemini = string.Empty;
        h.Collector.Leagues = [new("eng.1", "Premier League")];
        h.Espn = EspnRoutes(scoreboard: EspnClientTests.ScoreboardJson);
        h.Db.Fixtures.Add(new Fixture { EspnEventId = "402", LeagueSlug = "eng.1", StatusShort = "OLD", HomeTeamName = "x", AwayTeamName = "y" });
        await h.Db.SaveChangesAsync();

        await h.Build().RunAsync(CollectorMode.Full);

        await using var read = h.NewContext();
        var fixtures = await read.Fixtures.OrderBy(f => f.EspnEventId).ToListAsync();
        Assert.Equal(["401", "402"], fixtures.Select(f => f.EspnEventId)); // same events on 3 dates: no duplicates
        Assert.Equal((2, 1), (fixtures[0].HomeGoals, fixtures[0].AwayGoals));
        Assert.Equal("Premier League", fixtures[0].LeagueName);
        Assert.Equal("STATUS_SCHEDULED", fixtures[1].StatusShort);
        Assert.Equal("Manchester City", fixtures[1].HomeTeamName);
        Assert.Equal(3, h.EspnHandler!.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/scoreboard")));
    }

    [Fact]
    public async Task RunAsync_Full_SavesInjuriesFromEspnRoster_WithoutDuplicatesAcrossRuns()
    {
        await using var h = new PipelineHarness();
        await SeedArsenalAsync(h);
        h.ApiKeys.Gemini = string.Empty;
        h.Espn = EspnRoutes(roster: EspnClientTests.RosterJson);

        await h.Build().RunAsync(CollectorMode.Full);
        await h.Build().RunAsync(CollectorMode.Full); // undated injury listed again the next run

        await using var read = h.NewContext();
        var injuries = await read.PlayerInjuries.OrderBy(i => i.PlayerEspnId).ToListAsync();
        Assert.Equal(2, injuries.Count);
        Assert.Equal(("1001", "Hamstring - Strain - Leg", "Out"), (injuries[0].PlayerEspnId, injuries[0].Reason, injuries[0].Type));
        Assert.Equal(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), injuries[0].ReportedDateUtc);
        Assert.Equal(("1003", "Knock", "Arsenal", "359"), (injuries[1].PlayerEspnId, injuries[1].Reason, injuries[1].TeamName, injuries[1].TeamEspnId));
    }

    [Fact]
    public async Task RunAsync_Roster_UpsertsPlayersFromEspn()
    {
        await using var h = new PipelineHarness();
        var team = await SeedArsenalAsync(h);
        h.Espn = EspnRoutes(roster: EspnClientTests.RosterJson);
        h.Db.Players.Add(new Player { EspnId = "1001", FullName = "Old Name" });
        await h.Db.SaveChangesAsync();

        await h.Build().RunAsync(CollectorMode.Roster);

        await using var read = h.NewContext();
        var players = await read.Players.OrderBy(p => p.EspnId).ToListAsync();
        Assert.Equal(3, players.Count);
        Assert.All(players, p => Assert.Equal(team.Id, p.TeamId));
        Assert.Equal(("Bukayo Saka", 7), (players[0].FullName, players[0].Number));
    }

    [Fact]
    public async Task RunAsync_News_IngestsEspnTeamNews_ForLinkedTeams()
    {
        await using var h = new PipelineHarness();
        await SeedArsenalAsync(h);
        h.ApiKeys.Gemini = string.Empty;
        h.Espn = EspnRoutes(news: EspnClientTests.NewsJson);
        h.Db.Teams.Add(new Team { Name = "Not linked", ApiFootballId = 99 }); // no ESPN id: skipped
        await h.Db.SaveChangesAsync();

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        var article = await read.NewsArticles.SingleAsync();
        Assert.Equal(("ESPN", "Arsenal win again"), (article.SourceName, article.Title));
        var newsCall = Assert.Single(h.EspnHandler!.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/news"));
        Assert.Contains("team=359", newsCall.RequestUri!.Query);

        // Fetched for Arsenal: tagged with the team even though Gemini is off.
        var link = await read.ArticleEntities.SingleAsync();
        Assert.Equal(("Team", article.Id), (link.EntityType, link.ArticleId));
    }

    [Fact]
    public async Task RunAsync_News_TagsTeamsByKeyword_WithoutGemini_AndBackfillsOlderArticles()
    {
        await using var h = new PipelineHarness();
        var arsenal = await SeedArsenalAsync(h);
        var chelsea = new Team { EspnId = "363", LeagueSlug = "eng.1", Name = "Chelsea", IsTracked = true };
        h.Db.Teams.Add(chelsea);
        await h.Db.SaveChangesAsync();
        h.Db.WatchlistAliases.Add(new WatchlistAlias { EntityType = "Team", EntityId = chelsea.Id, Keyword = "Chelsea", Language = "en" });
        h.Db.NewsArticles.Add(new NewsArticle
        {
            UrlHash = "old", Url = "https://x.com/old", Title = "Chelsea sign a striker",
            PublishedAtUtc = DateTime.UtcNow.AddDays(-2), IsAiProcessed = true, IsNotified = true
        });
        await h.Db.SaveChangesAsync();
        h.ApiKeys.Gemini = string.Empty;
        h.Collector.SportsRssFeeds = [new("BBC Sport", "https://feeds.bbci.co.uk/sport/football/rss.xml", "en")];
        h.Rss = r => r.RequestUri!.Host.Contains("bbci")
            ? TestDbFactory.XmlResponse(TestDbFactory.Rss(
                ("Arsenal and Chelsea meet on Sunday", "https://bbc.com/preview"),
                ("Chelseanomics: not a team", "https://bbc.com/other-chelsea")))
            : TestDbFactory.XmlResponse(TestDbFactory.Rss());

        await h.Build().RunAsync(CollectorMode.News);

        await using var read = h.NewContext();
        var articles = await read.NewsArticles.Include(a => a.Entities).ToListAsync();
        Assert.Equal([arsenal.Id, chelsea.Id], articles.Single(a => a.Url == "https://bbc.com/preview").Entities.Select(e => e.EntityId).Order());
        Assert.Equal(chelsea.Id, Assert.Single(articles.Single(a => a.Url == "https://x.com/old").Entities).EntityId);
    }
}
