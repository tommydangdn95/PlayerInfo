using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlayerInfo.Api.Logging;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class LiveLogBufferTests
{
    [Fact]
    public void KeepsOnlyTheLatestEntries_WithIncreasingIds()
    {
        var buffer = new LiveLogBuffer();
        for (var i = 1; i <= LiveLogBuffer.Capacity + 5; i++)
        {
            buffer.Add("Info", "Test", $"m{i}", null, null, null);
        }

        var all = buffer.GetAfter(0);
        Assert.Equal(LiveLogBuffer.Capacity, all.Count);
        Assert.Equal(6, all[0].Id);
        Assert.Equal("m1005", all[^1].Message);
        Assert.Equal(["m1004", "m1005"], buffer.GetAfter(1003).Select(e => e.Message));
        Assert.Equal(["m1005"], buffer.GetAfter(0, max: 1).Select(e => e.Message));
    }

    [Fact]
    public void TruncatesLongMessages()
    {
        var buffer = new LiveLogBuffer();
        buffer.Add("Info", "Test", new string('x', LiveLogBuffer.MaxMessageLength + 100), null, null, null);
        Assert.Equal(LiveLogBuffer.MaxMessageLength + 1, buffer.GetAfter(0).Single().Message.Length);
    }

    [Fact]
    public async Task Subscribe_ReturnsBacklog_ThenLiveEntries_WithoutGapsOrDuplicates()
    {
        var buffer = new LiveLogBuffer();
        buffer.Add("Info", "Test", "old-1", null, null, null);
        buffer.Add("Info", "Test", "old-2", null, null, null);

        using var subscription = buffer.Subscribe(afterId: 1);
        buffer.Add("Warning", "Test", "new-3", null, "job1", "News");

        Assert.Equal(["old-2"], subscription.Backlog.Select(e => e.Message));
        var live = await subscription.Reader.ReadAsync();
        Assert.Equal(("new-3", "Warning", "job1"), (live.Message, live.Level, live.JobId));
        Assert.Equal(1, buffer.SubscriberCount);

        subscription.Dispose();
        Assert.Equal(0, buffer.SubscriberCount);
        buffer.Add("Info", "Test", "after-dispose", null, null, null); // must not throw
    }

    [Fact]
    public async Task Provider_CapturesOnlyAppCategories_AndTagsTheCurrentJob()
    {
        var buffer = new LiveLogBuffer();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new LiveLogLoggerProvider(buffer)));

        factory.CreateLogger("System.Net.Http.HttpClient.Telegram").LogWarning("https://api.telegram.org/bot<token>/sendMessage");
        var appLogger = factory.CreateLogger("PlayerInfo.Api.Services.CollectorPipeline");
        appLogger.LogDebug("debug is skipped");
        appLogger.LogInformation("outside job");

        using (JobLogContext.Begin("abc12345", "Full"))
        {
            await Task.Yield(); // the job id flows across awaits
            appLogger.LogError(new InvalidOperationException("boom"), "step failed");
        }

        appLogger.LogInformation("after job");

        var entries = buffer.GetAfter(0);
        Assert.Equal(["outside job", "step failed", "after job"], entries.Select(e => e.Message));
        Assert.All(entries, e => Assert.Equal("CollectorPipeline", e.Category));
        Assert.Equal(("Error", "abc12345", "Full", "InvalidOperationException: boom"),
            (entries[1].Level, entries[1].JobId, entries[1].JobMode, entries[1].Exception));
        Assert.Null(entries[0].JobId);
        Assert.Null(entries[2].JobId);
    }
}

public sealed class LogsEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("/api/logs")]
    [InlineData("/api/logs/stream")]
    public async Task RequiresLogin(string path)
        => Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path)).StatusCode);

    [Fact]
    public async Task QueuedJob_IsLoggedWithItsJobId()
    {
        var admin = await factory.CreateAdminClientAsync();
        var queued = await (await admin.PostAsync("/api/jobs/run?mode=news", null)).Content.ReadFromJsonAsync<JsonElement>();
        var jobId = queued.GetProperty("jobId").GetString();

        var entries = await admin.GetFromJsonAsync<JsonElement>("/api/logs?limit=1000");
        Assert.Contains(entries.EnumerateArray(), e => e.GetProperty("message").GetString()!.Contains($"Queued job {jobId}"));
    }

    [Fact]
    public async Task Stream_SendsBacklogThenNewEntries_AsServerSentEvents()
    {
        var buffer = factory.Services.GetRequiredService<LiveLogBuffer>();
        var marker = Guid.NewGuid().ToString("N");
        buffer.Add("Info", "Test", $"backlog-{marker}", null, null, null);

        var admin = await factory.CreateAdminClientAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/logs/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await admin.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        await ReadUntilAsync(reader, $"backlog-{marker}", cts.Token);

        buffer.Add("Warning", "Test", $"live-{marker}", null, "job42", "News");
        var line = await ReadUntilAsync(reader, $"live-{marker}", cts.Token);
        var entry = JsonDocument.Parse(line["data: ".Length..]).RootElement;
        Assert.Equal(("Warning", "job42"), (entry.GetProperty("level").GetString(), entry.GetProperty("jobId").GetString()));
    }

    private static async Task<string> ReadUntilAsync(StreamReader reader, string text, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains(text))
            {
                return line;
            }
        }

        throw new InvalidOperationException($"Stream ended before '{text}'.");
    }
}

public sealed class HttpTraceHandlerTests
{
    private static (HttpClient Client, LiveLogBuffer Buffer) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string provider = "Test")
    {
        var buffer = new LiveLogBuffer();
        var handler = new HttpTraceHandler(buffer, provider) { InnerHandler = new StubHttpMessageHandler(respond) };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") }, buffer);
    }

    [Theory]
    [InlineData("https://newsdata.io/api/1/latest?apikey=SECRET123&q=arsenal", "https://newsdata.io/api/1/latest?apikey=***&q=arsenal")]
    [InlineData("https://x.io/a?key=SECRET&Token=abc&page=2", "https://x.io/a?key=***&Token=***&page=2")]
    [InlineData("https://api.telegram.org/bot123456:ABC-def_SECRET/sendMessage", "https://api.telegram.org/bot***/sendMessage")]
    [InlineData("https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/scoreboard?dates=20261118", "https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/scoreboard?dates=20261118")]
    public void RedactUrl_MasksSecrets(string url, string expected)
        => Assert.Equal(expected, HttpTraceHandler.RedactUrl(new Uri(url)));

    [Fact]
    public async Task RecordsRequestAndResponse_AndCallerCanStillReadTheBody()
    {
        var (client, buffer) = Create(_ => TestDbFactory.JsonResponse("""{"ok":true,"items":[1,2]}"""), "Gemini");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/generate?key=SECRET")
        {
            Content = new StringContent("""{"prompt":"hi"}""", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", "HEADER-SECRET");

        using (JobLogContext.Begin("job12345", "News"))
        {
            var res = await client.SendAsync(request);
            Assert.Equal("""{"ok":true,"items":[1,2]}""", await res.Content.ReadAsStringAsync());
        }

        var entry = Assert.Single(buffer.GetAfter(0));
        Assert.Equal(("Info", "Gemini", "job12345"), (entry.Level, entry.Category, entry.JobId));
        var http = entry.Http!;
        Assert.Equal(("POST", "https://api.example.com/v1/generate?key=***", 200), (http.Method, http.Url, http.StatusCode));
        Assert.Equal("""{"prompt":"hi"}""", http.RequestBody);
        Assert.Equal("""{"ok":true,"items":[1,2]}""", http.ResponseBody);
        Assert.StartsWith("POST https://api.example.com/v1/generate?key=*** → 200", entry.Message);
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(entry));
    }

    [Fact]
    public async Task TruncatesLargeBodies_AndFlagsErrorsAsWarnings()
    {
        var big = new string('a', HttpTraceHandler.MaxResponseBodyChars + 500);
        var (client, buffer) = Create(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(big, System.Text.Encoding.UTF8, "text/plain")
        });

        var res = await client.GetAsync("limited");
        Assert.Equal(big.Length, (await res.Content.ReadAsStringAsync()).Length); // the caller still gets everything

        var entry = Assert.Single(buffer.GetAfter(0));
        Assert.Equal("Warning", entry.Level);
        Assert.Equal(429, entry.Http!.StatusCode);
        Assert.True(entry.Http.ResponseTruncated);
        Assert.Equal(HttpTraceHandler.MaxResponseBodyChars, entry.Http.ResponseBody!.Length);
    }

    [Fact]
    public async Task RecordsNetworkErrors_AndRethrows()
    {
        var (client, buffer) = Create(_ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("down"));

        var entry = Assert.Single(buffer.GetAfter(0));
        Assert.Equal("Warning", entry.Level);
        Assert.Null(entry.Http!.StatusCode);
        Assert.Contains("connection reset", entry.Http.Error);
    }
}
