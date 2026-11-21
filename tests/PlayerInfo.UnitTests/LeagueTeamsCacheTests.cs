using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Controllers;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class LeagueTeamsCacheTests
{
    private static (WatchlistController Controller, EspnClient Espn, StubHttpMessageHandler Handler) Create(
        PlayerInfo.Api.Data.AppDbContext db, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var options = new CollectorOptions();
        options.ApplyDefaults();
        var handler = new StubHttpMessageHandler(respond);
        var espn = new EspnClient(new HttpClient(handler) { BaseAddress = new Uri(EspnClient.BaseUrl) }, NullLogger<EspnClient>.Instance);
        return (new WatchlistController(db, Options.Create(options)), espn, handler);
    }

    private static List<WatchlistController.LeagueTeamResult> Teams(IActionResult result)
        => Assert.IsType<List<WatchlistController.LeagueTeamResult>>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task LeagueTeams_AreFetchedFromEspnOnce_ThenServedFromTheDatabase()
    {
        await using var db = TestDbFactory.CreateInMemory();
        db.Teams.Add(new Team { EspnId = "359", Name = "Arsenal" });
        await db.SaveChangesAsync();
        var (controller, espn, handler) = Create(db, _ => TestDbFactory.JsonResponse(EspnClientTests.TeamsJson));

        var first = Teams(await controller.GetLeagueTeamsAsync("eng.1", espn));
        var second = Teams(await controller.GetLeagueTeamsAsync("eng.1", espn));

        Assert.Single(handler.Requests); // the second visit did not call ESPN
        Assert.Equal(["AFC Bournemouth", "Arsenal"], second.Select(t => t.Name));
        Assert.NotNull(second.Single(t => t.EspnId == "359").ExistingTeamId);
        Assert.Equal(first.Select(t => t.EspnId), second.Select(t => t.EspnId));
        Assert.Equal(2, db.LeagueTeams.Count(t => t.LeagueSlug == "eng.1"));
    }

    [Fact]
    public async Task LeagueTeams_AreRefetched_InANewSeasonOrOnDemand_AndKeptWhenEspnFails()
    {
        await using var db = TestDbFactory.CreateInMemory();
        db.LeagueTeams.Add(new LeagueTeam
        {
            LeagueSlug = "eng.1", EspnId = "1", Name = "Old team",
            UpdatedAtUtc = DateTime.UtcNow.AddYears(-1) // fetched last season
        });
        await db.SaveChangesAsync();
        var espnDown = false;
        var (controller, espn, handler) = Create(db, _ => espnDown
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : TestDbFactory.JsonResponse(EspnClientTests.TeamsJson));

        // Last season's list: replaced by ESPN's current one.
        Assert.Equal(["AFC Bournemouth", "Arsenal"], Teams(await controller.GetLeagueTeamsAsync("eng.1", espn)).Select(t => t.Name));
        Assert.Single(handler.Requests);

        // Fresh, but the admin asks for an update; ESPN is down: the cached list stays.
        espnDown = true;
        Assert.Equal(["AFC Bournemouth", "Arsenal"], Teams(await controller.GetLeagueTeamsAsync("eng.1", espn, refresh: true)).Select(t => t.Name));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LeagueTeams_FetchedEarlierThisSeason_AreNotRefetched()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var startOfSeason = new DateTime(WatchlistController.SeasonOf(DateTime.UtcNow), WatchlistController.SeasonStartMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        db.LeagueTeams.Add(new LeagueTeam { LeagueSlug = "eng.1", EspnId = "1", Name = "Cached team", UpdatedAtUtc = startOfSeason.AddHours(1) });
        await db.SaveChangesAsync();
        var (controller, espn, handler) = Create(db, _ => TestDbFactory.JsonResponse(EspnClientTests.TeamsJson));

        Assert.Equal("Cached team", Assert.Single(Teams(await controller.GetLeagueTeamsAsync("eng.1", espn))).Name);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(2026, 11, 2026)]
    [InlineData(2027, 3, 2026)]
    [InlineData(2027, 7, 2026)]
    [InlineData(2027, 8, 2027)]
    public void SeasonOf_StartsInAugust(int year, int month, int season)
        => Assert.Equal(season, WatchlistController.SeasonOf(new DateTime(year, month, 15, 0, 0, 0, DateTimeKind.Utc)));
}
