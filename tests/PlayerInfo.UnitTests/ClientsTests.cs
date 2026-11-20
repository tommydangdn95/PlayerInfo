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

    private static GeminiEnricherClient CreateGemini(StubHttpMessageHandler handler, string key = "test-key")
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") },
            Options.Create(new ApiKeysOptions { Gemini = key }),
            Options.Create(new CollectorOptions()),
            NullLogger<GeminiEnricherClient>.Instance);

    [Fact]
    public async Task GeminiEnricherClient_ParsesStructuredJsonResponse_AndSendsKeyInHeader()
    {
        var handler = new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse(
            """[{"articleId":10,"isRelevant":true,"category":"Transfer","summaryVi":"Arsenal ký hợp đồng mới.","matchedEntities":["Arsenal"]}]""")));
        var client = CreateGemini(handler);

        var result = await client.EnrichBatchAsync([new NewsArticle { Id = 10, Title = "Arsenal sign new striker", SourceName = "BBC" }], ["Arsenal"]);

        Assert.True(result.Succeeded);
        var article = Assert.Single(result.Articles);
        Assert.True(article.IsRelevant);
        Assert.Equal("Transfer", article.Category);
        Assert.Equal("Arsenal ký hợp đồng mới.", article.SummaryVi);

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain("key=", request.RequestUri!.Query);
        Assert.Equal("test-key", request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task GeminiEnricherClient_ReportsFailure_OnHttpErrorOrUnparsableOutput()
    {
        var article = new NewsArticle { Id = 1, Title = "t", SourceName = "s" };

        var rateLimited = CreateGemini(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.False((await rateLimited.EnrichBatchAsync([article], [])).Succeeded);

        var garbage = CreateGemini(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(TestDbFactory.GeminiResponse("{not json"))));
        Assert.False((await garbage.EnrichBatchAsync([article], [])).Succeeded);

        var throwing = CreateGemini(new StubHttpMessageHandler(_ => throw new HttpRequestException("boom")));
        Assert.False((await throwing.EnrichBatchAsync([article], [])).Succeeded);
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
