using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class ClientsTests
{
    [Fact]
    public async Task RssNewsClient_ParsesItemsAndStripsHtml()
    {
        const string rssXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>Test Feed</title>
                <item>
                  <title>Arsenal sign new striker</title>
                  <link>https://example.com/arsenal-striker</link>
                  <description>&lt;p&gt;Breaking &lt;b&gt;transfer&lt;/b&gt; news.&lt;/p&gt;</description>
                  <pubDate>Tue, 17 Nov 2026 03:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;

        var http = new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.XmlResponse(rssXml)));
        var client = new RssNewsClient(http, NullLogger<RssNewsClient>.Instance);

        var items = await client.FetchGoogleNewsAsync("Arsenal", "en");

        Assert.Single(items);
        Assert.Equal("Arsenal sign new striker", items[0].Title);
        Assert.Equal("Breaking transfer news.", items[0].Snippet);
    }

    [Fact]
    public async Task RssNewsClient_GoogleNews_StripsPublisherSuffixFromTitle()
    {
        const string rssXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel><title>Google News</title>
              <item>
                <title>Arsenal sign new striker - BBC Sport</title>
                <link>https://news.google.com/rss/articles/CBMiXyz</link>
                <source url="https://www.bbc.co.uk">BBC Sport</source>
              </item>
            </channel></rss>
            """;

        var client = new RssNewsClient(
            new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.XmlResponse(rssXml))),
            NullLogger<RssNewsClient>.Instance);

        var items = await client.FetchGoogleNewsAsync("Arsenal", "en");

        Assert.Equal("Arsenal sign new striker", Assert.Single(items).Title);
        Assert.Equal("BBC Sport", items[0].SourceName);
    }

    [Fact]
    public async Task RssNewsClient_ReturnsEmpty_WhenHttpFails()
    {
        var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var client = new RssNewsClient(http, NullLogger<RssNewsClient>.Instance);

        var items = await client.FetchGoogleNewsAsync("Arsenal", "en");

        Assert.Empty(items);
    }

    /// <summary>A Gemini client whose HTTP calls go through the trace handler, which records them in ApiCalls.</summary>
    private static GeminiEnricherClient CreateGemini(
        StubHttpMessageHandler handler, string key = "test-key", List<string>? fallbacks = null, string? dbName = null,
        int dailyLimit = 0, List<GeminiModelLimit>? limits = null)
    {
        var recorder = TestDbFactory.CreateApiUsage(dbName ?? Guid.NewGuid().ToString());
        var trace = new PlayerInfo.Api.Logging.HttpTraceHandler(new PlayerInfo.Api.Logging.LiveLogBuffer(), "Gemini", recorder) { InnerHandler = handler };
        return new(
            new HttpClient(trace) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") },
            Options.Create(new ApiKeysOptions { Gemini = key }),
            Options.Create(new CollectorOptions
            {
                GeminiModel = "main-model",
                GeminiFallbackModels = fallbacks ?? [],
                GeminiDailyRequestLimit = dailyLimit,
                GeminiModelLimits = limits ?? []
            }),
            recorder,
            NullLogger<GeminiEnricherClient>.Instance);
    }

    private const string GeminiOk = """{"articles":[{"articleId":1,"isRelevant":true,"topic":"Other","claimType":"Report","importance":2,"summaryVi":"x","matchedEntities":[],"storyRef":"N1"}],"stories":[]}""";

    private static ApiCall Gemini(string model, string outcome, DateTime atUtc) => new() { Provider = "Gemini", Model = model, Outcome = outcome, AtUtc = atUtc };

    [Fact]
    public async Task GeminiEnricherClient_RecordsEveryAttempt_WithModelAndTokenUsage()
    {
        var dbName = Guid.NewGuid().ToString();
        var withUsage = System.Text.Json.JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = GeminiOk } } } } },
            usageMetadata = new { promptTokenCount = 1200, candidatesTokenCount = 300, thoughtsTokenCount = 50, totalTokenCount = 1550 }
        });
        var handler = new StubHttpMessageHandler(req => req.RequestUri!.AbsolutePath.Contains("main-model")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : TestDbFactory.JsonResponse(withUsage));

        using (PlayerInfo.Api.Logging.JobLogContext.Begin("job00001", "News"))
        {
            Assert.True((await CreateGemini(handler, fallbacks: ["backup"], dbName: dbName)
                .EnrichBatchAsync([new NewsArticle { Id = 1, Title = "t", SourceName = "s" }], [], [])).Succeeded);
        }

        await using var db = TestDbFactory.CreateInMemory(dbName);
        var calls = db.ApiCalls.OrderBy(c => c.Id).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(("main-model", 503, "Overloaded", 0), (calls[0].Model, calls[0].StatusCode, calls[0].Outcome, calls[0].TotalTokens));
        Assert.Equal(("backup", "Success", 1200, 300, 50, 1550), (calls[1].Model, calls[1].Outcome, calls[1].PromptTokens, calls[1].OutputTokens, calls[1].ThoughtTokens, calls[1].TotalTokens));
        Assert.All(calls, c => Assert.Equal(("Gemini", "job00001", 1), (c.Provider, c.JobId, c.ArticleCount)));
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/backup:generateContent", calls[1].Endpoint);
    }

    [Fact]
    public async Task GeminiEnricherClient_RateLimitedModel_IsSkippedForTheRun_AndTheNextModelAnswers()
    {
        var handler = new StubHttpMessageHandler(req => req.RequestUri!.AbsolutePath.Contains("main-model")
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse(GeminiOk)));
        var client = CreateGemini(handler, fallbacks: ["backup"]);
        var article = new NewsArticle { Id = 1, Title = "t", SourceName = "s" };

        Assert.True((await client.EnrichBatchAsync([article], [], [])).Succeeded);
        Assert.True((await client.EnrichBatchAsync([article], [], [])).Succeeded);

        // main-model answered 429 once and was not asked again in this run.
        Assert.Equal(
            ["main-model", "backup", "backup"],
            handler.Requests.Select(r => r.RequestUri!.AbsolutePath.Split('/')[^1].Split(':')[0]));
    }

    [Fact]
    public async Task GeminiEnricherClient_SkipsModelsThatUsedTheirDailyLimit_FailedCallsIncluded()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var db = TestDbFactory.CreateInMemory(dbName))
        {
            db.ApiCalls.Add(Gemini("main-model", "Overloaded", DateTime.UtcNow)); // failed, still counts
            db.ApiCalls.Add(Gemini("backup", "Success", DateTime.UtcNow.AddDays(-2))); // another quota day
            db.ApiCalls.Add(new ApiCall { Provider = "ESPN", AtUtc = DateTime.UtcNow }); // other providers never count
            await db.SaveChangesAsync();
        }

        var handler = new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse(GeminiOk)));
        var client = CreateGemini(handler, fallbacks: ["backup"], dbName: dbName, dailyLimit: 1);
        var article = new NewsArticle { Id = 1, Title = "t", SourceName = "s" };

        // The main model used its 1 request today: the fallback answers instead.
        Assert.True((await client.EnrichBatchAsync([article], [], [])).Succeeded);
        Assert.Equal("/v1beta/models/backup:generateContent", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);

        // Both used up: nothing is sent at all.
        Assert.False((await client.EnrichBatchAsync([article], [], [])).Succeeded);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GeminiEnricherClient_SkipsModelsAtTheirPerMinuteLimit()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var db = TestDbFactory.CreateInMemory(dbName))
        {
            db.ApiCalls.Add(Gemini("main-model", "Success", DateTime.UtcNow.AddSeconds(-20)));
            db.ApiCalls.Add(Gemini("main-model", "Success", DateTime.UtcNow.AddMinutes(-3))); // outside the minute
            await db.SaveChangesAsync();
        }

        var handler = new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse(GeminiOk)));
        var client = CreateGemini(handler, fallbacks: ["backup"], dbName: dbName,
            limits: [new("main-model", 500, 1), new("backup", 500, 15)]);

        Assert.True((await client.EnrichBatchAsync([new NewsArticle { Id = 1, Title = "t", SourceName = "s" }], [], [])).Succeeded);
        Assert.Equal("/v1beta/models/backup:generateContent", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public void GeminiQuotaDay_FollowsPacificMidnight()
    {
        // 07:59 UTC on 20 Nov is still 19 Nov in Los Angeles (UTC-8 in November); 08:00 UTC starts 20 Nov.
        Assert.Equal(new DateOnly(2026, 11, 19), GeminiQuotaDay.Of(new DateTime(2026, 11, 20, 7, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 11, 20), GeminiQuotaDay.Of(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), GeminiQuotaDay.StartUtc(new DateOnly(2026, 11, 20)));
    }

    [Fact]
    public async Task GeminiEnricherClient_FallsBackToTheNextModel_WhenOverloaded_ButNotOnBadRequest()
    {
        const string ok = GeminiOk;
        var article = new NewsArticle { Id = 1, Title = "t", SourceName = "s" };

        var overloaded = new StubHttpMessageHandler(req => req.RequestUri!.AbsolutePath.Contains("main-model")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : req.RequestUri.AbsolutePath.Contains("retired")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse(ok)));
        var result = await CreateGemini(overloaded, fallbacks: ["retired", "backup", "never-called"]).EnrichBatchAsync([article], [], []);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["/v1beta/models/main-model:generateContent", "/v1beta/models/retired:generateContent", "/v1beta/models/backup:generateContent"],
            overloaded.Requests.Select(r => r.RequestUri!.AbsolutePath));

        var badRequest = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        Assert.False((await CreateGemini(badRequest, fallbacks: ["backup"]).EnrichBatchAsync([article], [], [])).Succeeded);
        Assert.Single(badRequest.Requests);
    }

    [Fact]
    public void CollectorOptions_Defaults_HydrogenFirst_ThenFlash_ThenFlashLite_WithFreeTierLimits()
    {
        var options = new CollectorOptions();
        options.ApplyDefaults();

        Assert.Equal(
            ["gemini-4-hydrogen", "gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3-flash-preview", "gemini-2.5-flash", "gemini-2.5-flash-lite"],
            options.GeminiModels());
        Assert.Equal(new GeminiModelLimit("gemini-3.5-flash-lite", 500, 15, 250_000), options.LimitFor("gemini-3.5-flash-lite"));
        Assert.Equal((20, 5), (options.LimitFor("GEMINI-3.8-FLASH").RequestsPerDay, options.LimitFor("GEMINI-3.8-FLASH").RequestsPerMinute));
        Assert.Equal(new GeminiModelLimit("some-new-model", 20), options.LimitFor("some-new-model")); // GeminiDailyRequestLimit
    }

    [Fact]
    public async Task GeminiEnricherClient_ParsesStructuredJsonResponse_AndSendsKeyInHeader()
    {
        var handler = new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse(
            """
            {"articles":[{"articleId":10,"isRelevant":true,"topic":"Transfer","claimType":"Official","importance":5,
              "summaryVi":"Arsenal ký hợp đồng mới.","matchedEntities":["Arsenal"],"storyRef":"S7"}],
             "stories":[{"ref":"S7","headlineVi":"Arsenal chiêu mộ tiền đạo","summaryVi":"Đã chính thức.","topic":"Transfer","importance":5}]}
            """)));
        var client = CreateGemini(handler);

        var result = await client.EnrichBatchAsync(
            [new NewsArticle { Id = 10, Title = "Arsenal sign new striker", SourceName = "BBC" }],
            ["Arsenal"],
            [new OpenStory(7, "Arsenal", "Transfer", "Arsenal hỏi mua tiền đạo", null)]);

        Assert.True(result.Succeeded);
        var article = Assert.Single(result.Articles);
        Assert.True(article.IsRelevant);
        Assert.Equal(("Transfer", "Official", 5, "S7"), (article.Topic, article.ClaimType, article.Importance, article.StoryRef));
        Assert.Equal("Arsenal ký hợp đồng mới.", article.SummaryVi);
        Assert.Equal("Arsenal chiêu mộ tiền đạo", Assert.Single(result.Stories).HeadlineVi);

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain("key=", request.RequestUri!.Query);
        Assert.Equal("test-key", request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task GeminiEnricherClient_ReportsFailure_OnHttpErrorOrUnparsableOutput()
    {
        var article = new NewsArticle { Id = 1, Title = "t", SourceName = "s" };

        var rateLimited = CreateGemini(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.False((await rateLimited.EnrichBatchAsync([article], [], [])).Succeeded);

        var garbage = CreateGemini(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse("{not json"))));
        Assert.False((await garbage.EnrichBatchAsync([article], [], [])).Succeeded);

        // The old array-only format is rejected rather than silently ignored.
        var oldFormat = CreateGemini(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse("[]"))));
        Assert.False((await oldFormat.EnrichBatchAsync([article], [], [])).Succeeded);

        var throwing = CreateGemini(new StubHttpMessageHandler(_ => throw new HttpRequestException("boom")));
        Assert.False((await throwing.EnrichBatchAsync([article], [], [])).Succeeded);
    }

    [Fact]
    public void GeminiEnricherClient_Prompt_ListsOpenStoriesForGrouping()
    {
        var prompt = GeminiEnricherClient.BuildPrompt(
            [new NewsArticle { Id = 3, Title = "Saka out for weeks", SourceName = "BBC" }],
            ["Arsenal"],
            [new OpenStory(12, "Arsenal", "Injury", "Saka chấn thương", "Saka đau gân kheo.")]);

        Assert.Contains("- S12 | Arsenal | Injury | Saka chấn thương | Saka đau gân kheo.", prompt);
        Assert.Contains("ID=3 | Source=BBC | Title=Saka out for weeks", prompt);
    }

    [Fact]
    public void TelegramNotifier_BuildStoryMessage_ShowsHeadlineConfidenceSourcesAndLink()
    {
        var story = new Story
        {
            HeadlineVi = "Arsenal & Chelsea <đàm phán>", SummaryVi = "Hai CLB đang đàm phán.", Topic = "Transfer",
            Importance = 3, Confidence = "Confirmed", SourceCount = 4
        };
        var hot = new Story { HeadlineVi = "Arteta bị sa thải", Topic = "Manager", Importance = 5, Confidence = "Official", SourceCount = 9 };

        var (text, included) = TelegramNotifier.BuildStoryMessage(
            [new StoryDigestItem(story, ["Arsenal", "Chelsea"], "https://bbc.com/x?a=1&b=2", "BBC Sport"), new StoryDigestItem(hot, [], null, null)],
            "News", DateTime.UtcNow);

        Assert.Equal(2, included);
        Assert.Contains("🔁 <b>Arsenal &amp; Chelsea &lt;đàm phán&gt;</b>", text);
        Assert.Contains("<i>Arsenal, Chelsea · 🟢 Nhiều nguồn uy tín · 4 nguồn</i>", text);
        Assert.Contains("<a href=\"https://bbc.com/x?a=1&amp;b=2\">BBC Sport</a>", text);
        Assert.Contains("🔥 <b>Arteta bị sa thải</b>", text);
        Assert.Contains("✅ Chính thức · 9 nguồn", text);
    }

    [Fact]
    public async Task ApiFootballClient_StopsCallingApi_WhenDailyQuotaReached()
    {
        var quota = TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString());
        await quota.IncrementAsync("ApiFootball", 90);

        var handler = new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse("{\"response\":[]}"));
        var client = new ApiFootballClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://v3.api-sports.io/") },
            quota,
            Options.Create(new ApiKeysOptions { ApiFootball = "test-key" }),
            Options.Create(new CollectorOptions { ApiFootballDailyLimit = 90 }),
            NullLogger<ApiFootballClient>.Instance);

        var transfers = await client.GetTransfersByTeamAsync(42);

        Assert.Empty(transfers);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApiFootballClient_CountsQuota_EvenWhenRequestThrows_AndHandlesErrorsField()
    {
        var quota = TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString());
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => ++calls == 1
            ? throw new HttpRequestException("connection reset")
            : TestDbFactory.JsonResponse("""{"errors":{"plan":"Free plans do not have access to this season"},"response":[]}"""));

        var client = new ApiFootballClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://v3.api-sports.io/") },
            quota,
            Options.Create(new ApiKeysOptions { ApiFootball = "test-key" }),
            Options.Create(new CollectorOptions()),
            NullLogger<ApiFootballClient>.Instance);

        Assert.Empty(await client.GetTransfersByTeamAsync(42));
        Assert.Empty(await client.GetTransfersByTeamAsync(42));
        Assert.Equal(2, await quota.GetTodayUsageAsync("ApiFootball"));
    }

    [Fact]
    public void NewsDataClient_BuildQuery_QuotesPhrasesAndRespectsLengthLimit()
    {
        var query = NewsDataClient.BuildQuery(["Arsenal", "Manchester United", "Bukayo Saka", "Declan Rice", "Martin Odegaard", "William Saliba", "Gabriel Jesus"]);

        Assert.StartsWith("Arsenal OR \"Manchester United\" OR \"Bukayo Saka\"", query);
        Assert.True(query.Length <= 100);
        Assert.Equal(string.Empty, NewsDataClient.BuildQuery([]));
    }

    [Fact]
    public void TelegramNotifier_BuildMessage_MarksHotNews()
    {
        var articles = new List<NewsArticle>
        {
            new() { Id = 1, Title = "Big news", Url = "https://a/1", SourceName = "BBC", Category = "Transfer", IsHot = true },
            new() { Id = 2, Title = "Small news", Url = "https://a/2", SourceName = "BBC", Category = "Other" }
        };

        var (text, _) = TelegramNotifier.BuildMessage(articles, "News", DateTime.UtcNow);

        Assert.Contains("🔥 <b>[Transfer]</b> Big news", text);
        Assert.Contains("• <b>[Other]</b> Small news", text);
    }

    [Fact]
    public void TelegramNotifier_BuildMessage_EscapesHtmlAndStaysUnderLimit()
    {
        var articles = Enumerable.Range(1, 40).Select(i => new NewsArticle
        {
            Url = $"https://example.com/a?x=1&y=\"{i}\"",
            Title = $"Title {i}",
            SummaryVi = new string('x', 300) + " <b>",
            SourceName = "BBC & Co",
            Category = "Other"
        }).ToList();

        var (text, included) = TelegramNotifier.BuildMessage(articles, "News", DateTime.UtcNow);

        Assert.InRange(included, 1, 39);
        Assert.True(text.Length <= TelegramNotifier.MaxMessageLength);
        Assert.Contains("BBC &amp; Co", text);
        Assert.Contains("&lt;b&gt;", text);
        Assert.Contains("href=\"https://example.com/a?x=1&amp;y=&quot;1&quot;\"", text);
    }

    [Fact]
    public void CollectorOptions_BindingConfiguredLists_DoesNotDuplicateDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collector:Leagues:0:Slug"] = "eng.1",
                ["Collector:Leagues:0:Name"] = "Premier League",
                ["Collector:Leagues:1:Slug"] = " ESP.1 ",
                ["Collector:Leagues:1:Name"] = "La Liga",
                ["Collector:Leagues:2:Slug"] = "eng.1",
                ["Collector:Leagues:2:Name"] = "Duplicate",
                ["Collector:SportsRssFeeds:0:Name"] = "BBC Sport",
                ["Collector:SportsRssFeeds:0:Url"] = "https://feeds.bbci.co.uk/sport/football/rss.xml",
                ["Collector:SportsRssFeeds:0:Language"] = "en"
            })
            .Build();

        var options = new ServiceCollection()
            .AddOptions<CollectorOptions>().Bind(config.GetSection("Collector")).PostConfigure(o => o.ApplyDefaults())
            .Services.BuildServiceProvider()
            .GetRequiredService<IOptions<CollectorOptions>>().Value;

        Assert.Equal(["eng.1", "esp.1"], options.Leagues.Select(l => l.Slug));
        Assert.Equal("BBC Sport", Assert.Single(options.SportsRssFeeds).Name);
    }

    [Fact]
    public void CollectorOptions_ApplyDefaults_FillsEmptyLists()
    {
        var options = new CollectorOptions();
        options.ApplyDefaults();

        Assert.Equal(8, options.Leagues.Count);
        Assert.Contains(options.Leagues, l => l.Slug == "uefa.champions");
        Assert.Equal(CollectorOptions.DefaultSportsRssFeeds.Count, options.SportsRssFeeds.Count);
    }
}
