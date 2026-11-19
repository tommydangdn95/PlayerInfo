using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PlayerInfo.Api.Clients;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

/// <summary>ESPN client parsing, against small hand-written payloads shaped like site.api.espn.com responses.</summary>
public sealed class EspnClientTests
{
    public const string TeamsJson = """
        {"sports":[{"leagues":[{"teams":[
          {"team":{"id":"359","displayName":"Arsenal","shortDisplayName":"Arsenal","abbreviation":"ARS","logos":[{"href":"https://a.espncdn.com/i/teamlogos/soccer/500/359.png"}]}},
          {"team":{"id":"349","displayName":"AFC Bournemouth","shortDisplayName":"Bournemouth","abbreviation":"BOU","logos":[]}},
          {"team":{"displayName":"No id"}}
        ]}]}]}
        """;

    public const string ScoreboardJson = """
        {"events":[
          {"id":"401","date":"2026-11-18T20:00Z","competitions":[{"date":"2026-11-18T20:00Z",
            "status":{"type":{"name":"STATUS_FULL_TIME","state":"post","completed":true}},
            "competitors":[
              {"homeAway":"home","score":"2","team":{"id":"359","displayName":"Arsenal"}},
              {"homeAway":"away","score":"1","team":{"id":"349","displayName":"AFC Bournemouth"}}]}]},
          {"id":"402","date":"2026-11-19T15:00Z","competitions":[{"date":"2026-11-19T15:00Z",
            "status":{"type":{"name":"STATUS_SCHEDULED","state":"pre","completed":false}},
            "competitors":[
              {"homeAway":"home","score":"0","team":{"id":"382","displayName":"Manchester City"}},
              {"homeAway":"away","score":"0","team":{"id":"360","displayName":"Manchester United"}}]}]},
          {"id":"403","competitions":[]}
        ]}
        """;

    public const string RosterJson = """
        {"athletes":[
          {"id":"1001","displayName":"Bukayo Saka","jersey":"7","position":{"displayName":"Forward","abbreviation":"F"},
           "headshot":{"href":"https://a.espncdn.com/saka.png"},
           "injuries":[{"status":"Out","date":"2026-11-15T10:00Z","details":{"type":"Hamstring","detail":"Strain","location":"Leg"}}]},
          {"id":"1002","displayName":"Declan Rice","jersey":"41","position":{"abbreviation":"M"},"injuries":[]},
          {"id":"1003","fullName":"Undated Injury","injuries":[{"status":"Day-To-Day","type":{"description":"Knock"}}]}
        ]}
        """;

    public const string NewsJson = """
        {"articles":[
          {"headline":"Arsenal win again","description":"Report.","published":"2026-11-19T08:00:00Z","links":{"web":{"href":"https://www.espn.com/soccer/story/_/id/1/arsenal"}}},
          {"headline":"No link"}
        ]}
        """;

    private static (EspnClient Client, StubHttpMessageHandler Handler) Create(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(_ => TestDbFactory.JsonResponse(json, status));
        return (new EspnClient(new HttpClient(handler) { BaseAddress = new Uri(EspnClient.BaseUrl) }, NullLogger<EspnClient>.Instance), handler);
    }

    [Fact]
    public async Task GetLeagueTeams_ParsesTeamsSortedByName_AndSkipsIncompleteEntries()
    {
        var (client, handler) = Create(TeamsJson);

        var teams = await client.GetLeagueTeamsAsync("eng.1");

        Assert.Equal(["AFC Bournemouth", "Arsenal"], teams.Select(t => t.Name));
        Assert.Equal("https://a.espncdn.com/i/teamlogos/soccer/500/359.png", teams.Single(t => t.Id == "359").LogoUrl);
        Assert.Null(teams.Single(t => t.Id == "349").LogoUrl);
        Assert.Equal("/apis/site/v2/sports/soccer/eng.1/teams", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetScoreboard_ParsesScores_OnlyForStartedMatches()
    {
        var (client, handler) = Create(ScoreboardJson);

        var matches = await client.GetScoreboardAsync("eng.1", new DateOnly(2026, 11, 18));

        Assert.Equal(2, matches.Count);
        var finished = matches.Single(m => m.EventId == "401");
        Assert.Equal((2, 1), (finished.HomeScore, finished.AwayScore));
        Assert.Equal("STATUS_FULL_TIME", finished.StatusName);
        Assert.Equal(new DateTime(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc), finished.KickoffUtc);
        Assert.Equal(DateTimeKind.Utc, finished.KickoffUtc.Kind);

        var scheduled = matches.Single(m => m.EventId == "402");
        Assert.Null(scheduled.HomeScore); // ESPN sends "0" before kick-off
        Assert.Equal("360", scheduled.AwayTeamId);

        Assert.Equal("?dates=20261118", handler.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task GetRoster_ParsesPlayersAndInjuries()
    {
        var (client, _) = Create(RosterJson);

        var athletes = await client.GetRosterAsync("eng.1", "359");

        Assert.Equal(3, athletes.Count);
        var saka = athletes.Single(a => a.Id == "1001");
        Assert.Equal((7, "Forward"), (saka.Jersey, saka.Position));
        var injury = Assert.Single(saka.Injuries);
        Assert.Equal("Out", injury.Status);
        Assert.Equal("Hamstring - Strain - Leg", injury.Reason);
        Assert.Equal(new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc), injury.DateUtc);

        Assert.Empty(athletes.Single(a => a.Id == "1002").Injuries);

        var undated = Assert.Single(athletes.Single(a => a.Id == "1003").Injuries);
        Assert.Equal(("Day-To-Day", "Knock", (DateTime?)null), (undated.Status, undated.Reason, undated.DateUtc));
    }

    [Fact]
    public async Task GetTeamNews_ReturnsArticlesWithLinks()
    {
        var (client, handler) = Create(NewsJson);

        var items = await client.GetTeamNewsAsync("eng.1", "359", 10);

        var item = Assert.Single(items);
        Assert.Equal(("Arsenal win again", "ESPN", "en"), (item.Title, item.SourceName, item.Language));
        Assert.Equal("https://www.espn.com/soccer/story/_/id/1/arsenal", item.Url);
        Assert.Equal("?team=359&limit=10", handler.Requests.Single().RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"unexpected\":true}")]
    public async Task FailuresAndUnexpectedPayloads_ReturnEmpty(HttpStatusCode status, string body)
    {
        var (client, _) = Create(body, status);

        Assert.Empty(await client.GetLeagueTeamsAsync("eng.1"));
        Assert.Empty(await client.GetScoreboardAsync("eng.1", new DateOnly(2026, 11, 18)));
        Assert.Empty(await client.GetRosterAsync("eng.1", "359"));
        Assert.Empty(await client.GetTeamNewsAsync("eng.1", "359", 5));
    }
}
