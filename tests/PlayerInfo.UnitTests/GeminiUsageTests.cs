using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Controllers;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Logging;
using PlayerInfo.Api.Services;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class ApiUsageTests
{
    [Fact]
    public async Task TraceHandler_RecordsEveryProvidersCalls_WithoutSecrets()
    {
        var dbName = Guid.NewGuid().ToString();
        var handler = new HttpTraceHandler(new LiveLogBuffer(), "NewsData", TestDbFactory.CreateApiUsage(dbName))
        {
            InnerHandler = new StubHttpMessageHandler(req => req.RequestUri!.Query.Contains("q=down")
                ? throw new HttpRequestException("reset")
                : TestDbFactory.JsonResponse("{}", HttpStatusCode.TooManyRequests))
        };
        using var client = new HttpClient(handler);

        using (JobLogContext.Begin("job42", "News"))
        {
            await client.GetAsync("https://newsdata.io/api/1/latest?apikey=SECRET&q=arsenal");
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://newsdata.io/api/1/latest?apikey=SECRET&q=down"));
        }

        await using var db = TestDbFactory.CreateInMemory(dbName);
        var calls = db.ApiCalls.OrderBy(c => c.Id).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(("NewsData", 429, "RateLimited", "job42"), (calls[0].Provider, calls[0].StatusCode, calls[0].Outcome, calls[0].JobId));
        Assert.Equal(("NetworkError", (int?)null), (calls[1].Outcome, calls[1].StatusCode));
        Assert.All(calls, c => Assert.Equal("https://newsdata.io/api/1/latest", c.Endpoint)); // no query string, no key
        Assert.Null(calls[0].Model);
    }

    [Fact]
    public async Task UsageController_SummarisesEachProvider_ByHourAndDay()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = TestDbFactory.CreateInMemory(dbName);
        var (dayStart, _) = VnDay.UtcRange(VnDay.Today());
        db.ApiCalls.AddRange(
            new ApiCall { Provider = "ESPN", Outcome = "Success", AtUtc = dayStart.AddHours(8).AddMinutes(5), DurationMs = 100, ResponseBytes = 1000 },
            new ApiCall { Provider = "ESPN", Outcome = "ServerError", StatusCode = 500, AtUtc = dayStart.AddHours(8).AddMinutes(10), DurationMs = 300 },
            new ApiCall { Provider = "Gemini", Model = "m", Outcome = "Success", AtUtc = dayStart.AddHours(9), TotalTokens = 1500, PromptTokens = 1000, OutputTokens = 500, DurationMs = 5000 },
            new ApiCall { Provider = "ESPN", Outcome = "Success", AtUtc = dayStart.AddHours(-1) }); // yesterday (VN)
        await db.SaveChangesAsync();
        var options = new CollectorOptions();
        options.ApplyDefaults();

        var result = await new UsageController(db, TestDbFactory.CreateQuotaManager(dbName), Options.Create(options)).GetAsync(days: 7);
        var usage = Assert.IsType<UsageController.UsageDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal((3, 1, 1500L), (usage.Today.Calls, usage.Today.Failed, usage.Today.TotalTokens));
        Assert.Equal(UsageController.Providers, usage.Providers.Select(p => p.Provider)); // every provider, even unused
        var espn = usage.Providers.Single(p => p.Provider == "ESPN");
        Assert.Equal((2, 1, 200, 300, 1000L), (espn.Totals.Calls, espn.Totals.Failed, espn.Totals.AvgDurationMs, espn.Totals.MaxDurationMs, espn.Totals.ResponseBytes));
        Assert.Equal(2, espn.CallsByHour[8]);
        Assert.Equal(1, espn.FailedByHour[8]);
        Assert.Equal(1, usage.CallsByHour[9]);
        Assert.Equal((0, options.ApiFootballDailyLimit), (usage.Providers.Single(p => p.Provider == "API-Football").QuotaUsed, usage.Providers.Single(p => p.Provider == "API-Football").QuotaLimit));

        var espnHistory = usage.History.Single(h => h.Provider == "ESPN").Days;
        Assert.Equal(7, espnHistory.Count);
        Assert.Equal((2, 1), (espnHistory[^1].Calls, espnHistory[^2].Calls));
    }
}

public sealed class GeminiControllerTests
{
    [Fact]
    public async Task Usage_ShowsEachModelAgainstItsLimits_InChainOrder()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = TestDbFactory.CreateInMemory(dbName);
        var todayStart = GeminiQuotaDay.StartUtc(GeminiQuotaDay.Today());
        var minute = todayStart.AddHours(2);
        db.ApiCalls.AddRange(
            new ApiCall { Provider = "Gemini", Model = "m1", Outcome = "Success", StatusCode = 200, AtUtc = minute.AddSeconds(5), PromptTokens = 100, OutputTokens = 20, TotalTokens = 120, ArticleCount = 15, DurationMs = 1000 },
            new ApiCall { Provider = "Gemini", Model = "m1", Outcome = "RateLimited", StatusCode = 429, AtUtc = minute.AddSeconds(40), DurationMs = 300 },
            new ApiCall { Provider = "Gemini", Model = "m2", Outcome = "Overloaded", StatusCode = 503, AtUtc = minute.AddMinutes(5), DurationMs = 500 },
            new ApiCall { Provider = "Gemini", Model = "m1", Outcome = "Success", StatusCode = 200, AtUtc = todayStart.AddHours(-1), TotalTokens = 999 }, // yesterday
            new ApiCall { Provider = "ESPN", Outcome = "Success", AtUtc = minute }); // not Gemini
        await db.SaveChangesAsync();

        var options = new CollectorOptions
        {
            GeminiModel = "m1",
            GeminiFallbackModels = ["m2"],
            GeminiModelLimits = [new("m1", 20, 5, 250_000), new("spare", 500, 15, 250_000)]
        };
        var client = new GeminiEnricherClient(new HttpClient(), Options.Create(new ApiKeysOptions { Gemini = "k" }), Options.Create(options),
            TestDbFactory.CreateApiUsage(dbName), NullLogger<GeminiEnricherClient>.Instance);

        var result = await new GeminiController(db, client, Options.Create(options)).GetUsageAsync(days: 7);
        var usage = Assert.IsType<GeminiController.UsageDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.True(usage.Configured);
        Assert.Equal((3, 1, 120L), (usage.Today.Calls, usage.Today.Success, usage.Today.TotalTokens));
        Assert.Equal(["m1", "m2", "spare"], usage.Models.Select(m => m.Model));
        Assert.Equal([1, 2, (int?)null], usage.Models.Select(m => m.Order));

        var m1 = usage.Models[0];
        Assert.Equal((20, 5, 250_000, 2, 2, 120L, 1, "RateLimited"),
            (m1.RequestsPerDay, m1.RequestsPerMinute, m1.TokensPerMinute, m1.UsedToday, m1.PeakRequestsPerMinute, m1.PeakTokensPerMinute, m1.RateLimitedToday, m1.LastOutcome));
        Assert.Equal((20, 0, 1, 1), (usage.Models[1].RequestsPerDay, usage.Models[1].RequestsPerMinute, usage.Models[1].UsedToday, usage.Models[1].OverloadedToday)); // unlisted: default daily limit

        Assert.Equal(7, usage.Daily.Count);
        Assert.Equal(999L, usage.Daily[^2].TotalTokens);
        Assert.Equal(4, usage.Recent.Count);
    }
}
