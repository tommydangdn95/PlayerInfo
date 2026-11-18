using System.Net;
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
    public async Task RssNewsClient_ReturnsEmpty_WhenHttpFails()
    {
        var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var client = new RssNewsClient(http, NullLogger<RssNewsClient>.Instance);

        var items = await client.FetchGoogleNewsAsync("Arsenal", "en");

        Assert.Empty(items);
    }

    [Fact]
    public async Task GeminiEnricherClient_ParsesStructuredJsonResponse()
    {
        const string geminiResponse = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "text": "[{\"articleId\":10,\"isRelevant\":true,\"category\":\"Transfer\",\"summaryVi\":\"Arsenal ký hợp đồng mới.\",\"matchedEntities\":[\"Arsenal\"]}]"
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var http = new HttpClient(new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(geminiResponse)))
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };

        var client = new GeminiEnricherClient(
            http,
            Options.Create(new ApiKeysOptions { Gemini = "test-key" }),
            Options.Create(new CollectorOptions()),
            NullLogger<GeminiEnricherClient>.Instance);

        var articles = new List<NewsArticle>
        {
            new() { Id = 10, Title = "Arsenal sign new striker", SourceName = "BBC" }
        };

        var results = await client.EnrichBatchAsync(articles, ["Arsenal"]);

        Assert.Single(results);
        Assert.True(results[0].IsRelevant);
        Assert.Equal("Transfer", results[0].Category);
        Assert.Equal("Arsenal ký hợp đồng mới.", results[0].SummaryVi);
    }

    [Fact]
    public async Task ApiFootballClient_StopsCallingApi_WhenDailyQuotaReached()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var quota = new QuotaManager(db, NullLogger<QuotaManager>.Instance);
        await quota.IncrementAsync("ApiFootball", 90);

        var httpCalled = false;
        var http = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            httpCalled = true;
            return TestDbFactory.JsonResponse("{\"response\":[]}");
        }))
        {
            BaseAddress = new Uri("https://v3.api-sports.io/")
        };

        var client = new ApiFootballClient(
            http,
            quota,
            Options.Create(new ApiKeysOptions { ApiFootball = "test-key" }),
            Options.Create(new CollectorOptions { ApiFootballDailyLimit = 90 }),
            NullLogger<ApiFootballClient>.Instance);

        var injuries = await client.GetInjuriesByLeagueAsync(39, 2026);

        Assert.Empty(injuries);
        Assert.False(httpCalled);
    }
}
