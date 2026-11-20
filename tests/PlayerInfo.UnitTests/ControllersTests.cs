using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Controllers;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class ControllersTests
{
    private static WatchlistController CreateWatchlist(PlayerInfo.Api.Data.AppDbContext db)
    {
        var options = new CollectorOptions();
        options.ApplyDefaults();
        return new WatchlistController(db, Options.Create(options));
    }

    [Fact]
    public async Task JobsController_Run_ReturnsBadRequest_WhenModeInvalid()
    {
        var controller = new JobsController(new JobQueue(), TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString()), Microsoft.Extensions.Logging.Abstractions.NullLogger<JobsController>.Instance);

        var result = await controller.RunAsync("invalid_mode");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("transfers_news")]
    [InlineData("news")]
    [InlineData("ROSTER")]
    public async Task JobsController_Run_ReturnsAccepted_ForValidModes(string mode)
    {
        var controller = new JobsController(new JobQueue(), TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString()), Microsoft.Extensions.Logging.Abstractions.NullLogger<JobsController>.Instance);

        var result = await controller.RunAsync(mode);

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public async Task NewsController_GetArticles_ExcludesIrrelevantAndFiltersByCategory()
    {
        await using var db = TestDbFactory.CreateInMemory();
        db.NewsArticles.AddRange(
            new NewsArticle { UrlHash = "h1", Url = "https://a.com/1", Title = "Injury News", Category = "Injury", IsRelevant = true, PublishedAtUtc = DateTime.UtcNow },
            new NewsArticle { UrlHash = "h2", Url = "https://a.com/2", Title = "Betting Spam", Category = "Injury", IsRelevant = false, PublishedAtUtc = DateTime.UtcNow },
            new NewsArticle { UrlHash = "h3", Url = "https://a.com/3", Title = "Transfer News", Category = "Transfer", IsRelevant = true, PublishedAtUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var controller = new NewsController(db);
        var result = await controller.GetArticlesAsync(category: "Injury");

        var ok = Assert.IsType<OkObjectResult>(result);
        var itemsProp = ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value) as System.Collections.IEnumerable;
        var count = itemsProp!.Cast<object>().Count();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NewsController_GetArticles_TagsTeams_AndFiltersByTeamAndDate()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var arsenal = new Team { EspnId = "359", Name = "Arsenal", LogoUrl = "https://logo/359.png" };
        var chelsea = new Team { EspnId = "363", Name = "Chelsea" };
        db.Teams.AddRange(arsenal, chelsea);
        await db.SaveChangesAsync();
        var saka = new Player { EspnId = "1001", FullName = "Bukayo Saka", TeamId = arsenal.Id };
        db.Players.Add(saka);

        var day = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var derby = new NewsArticle { UrlHash = "a", Url = "https://a/1", Title = "Derby", PublishedAtUtc = day };
        var sakaNews = new NewsArticle { UrlHash = "b", Url = "https://a/2", Title = "Saka injury", PublishedAtUtc = day.AddDays(-1) };
        var general = new NewsArticle { UrlHash = "c", Url = "https://a/3", Title = "League news", PublishedAtUtc = day };
        db.NewsArticles.AddRange(derby, sakaNews, general);
        await db.SaveChangesAsync();
        db.ArticleEntities.AddRange(
            new ArticleEntity { ArticleId = derby.Id, EntityType = "Team", EntityId = arsenal.Id },
            new ArticleEntity { ArticleId = derby.Id, EntityType = "Team", EntityId = chelsea.Id },
            new ArticleEntity { ArticleId = sakaNews.Id, EntityType = "Player", EntityId = saka.Id });
        await db.SaveChangesAsync();
        var controller = new NewsController(db);

        static List<NewsController.ArticleDto> Items(IActionResult r)
            => ((IEnumerable<NewsController.ArticleDto>)Assert.IsType<OkObjectResult>(r).Value!.GetType().GetProperty("items")!
                .GetValue(Assert.IsType<OkObjectResult>(r).Value)!).ToList();

        var all = Items(await controller.GetArticlesAsync());
        Assert.Equal(3, all.Count);
        Assert.Equal(["Arsenal", "Chelsea"], all.Single(a => a.Title == "Derby").Teams.Select(t => t.Name));
        Assert.Equal("Arsenal", Assert.Single(all.Single(a => a.Title == "Saka injury").Teams).Name); // via the player
        Assert.Empty(all.Single(a => a.Title == "League news").Teams);

        var arsenalNews = Items(await controller.GetArticlesAsync(teamId: arsenal.Id));
        Assert.Equal(["Derby", "Saka injury"], arsenalNews.Select(a => a.Title));

        var onDay = Items(await controller.GetArticlesAsync(teamId: arsenal.Id, fromUtc: day.Date, toUtc: day.Date.AddDays(1)));
        Assert.Equal("Derby", Assert.Single(onDay).Title);
    }

    [Fact]
    public async Task WatchlistController_CrudAliases_WorksAndReturnsNotFoundForMissingId()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var team = new Team { EspnId = "359", LeagueSlug = "eng.1", Name = "Arsenal" };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        var controller = CreateWatchlist(db);

        var created = await controller.AddAliasAsync(new WatchlistController.CreateAliasRequest("team", team.Id, " Arsenal ", "EN"), CancellationToken.None);
        var createdResult = Assert.IsType<CreatedResult>(created);
        var alias = Assert.IsType<WatchlistAlias>(createdResult.Value);
        Assert.Equal("Team", alias.EntityType);
        Assert.Equal("Arsenal", alias.Keyword);
        Assert.Equal("en", alias.Language);

        var duplicate = await controller.AddAliasAsync(new WatchlistController.CreateAliasRequest("Team", team.Id, "arsenal", "en"), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicate);

        var deleteOk = await controller.DeleteAliasAsync(alias.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteOk);

        var deleteMissing = await controller.DeleteAliasAsync(999, CancellationToken.None);
        Assert.IsType<NotFoundResult>(deleteMissing);
    }

    [Theory]
    [InlineData("Coach", 1, "Arsenal", "en")]   // unknown entity type
    [InlineData("Team", 999, "Arsenal", "en")]  // entity does not exist
    [InlineData("Team", 1, "   ", "en")]        // empty keyword
    [InlineData("Team", 1, "Arsenal", "fr")]    // unsupported language
    public async Task WatchlistController_AddAlias_ReturnsBadRequest_ForInvalidInput(string type, int entityId, string keyword, string language)
    {
        await using var db = TestDbFactory.CreateInMemory();
        db.Teams.Add(new Team { Id = 1, EspnId = "359", Name = "Arsenal" });
        await db.SaveChangesAsync();
        var controller = CreateWatchlist(db);

        var result = await controller.AddAliasAsync(new WatchlistController.CreateAliasRequest(type, entityId, keyword, language), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task WatchlistController_AddTeam_ReturnsConflict_ForDuplicateEspnId()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var controller = CreateWatchlist(db);

        var first = await controller.AddTeamAsync(new WatchlistController.CreateTeamRequest("360", "eng.1", "Manchester United"), CancellationToken.None);
        var second = await controller.AddTeamAsync(new WatchlistController.CreateTeamRequest("360", "eng.1", "Man Utd"), CancellationToken.None);

        Assert.IsType<CreatedResult>(first);
        Assert.IsType<ConflictObjectResult>(second);
    }

    [Theory]
    [InlineData("abc", "eng.1", "Name")]     // ESPN id must be numeric
    [InlineData("360", "xyz.9", "Name")]     // league not configured
    [InlineData("360", "eng.1", " ")]        // empty name
    public async Task WatchlistController_AddTeam_ValidatesInput(string espnId, string league, string name)
    {
        await using var db = TestDbFactory.CreateInMemory();
        var result = await CreateWatchlist(db).AddTeamAsync(new WatchlistController.CreateTeamRequest(espnId, league, name), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task WatchlistController_LinksLegacyTeam_ToEspnAndApiFootball()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var legacy = new Team { ApiFootballId = 33, Name = "Manchester United" }; // added before the ESPN switch
        db.Teams.AddRange(legacy, new Team { EspnId = "359", LeagueSlug = "eng.1", ApiFootballId = 42, Name = "Arsenal" });
        await db.SaveChangesAsync();
        var controller = CreateWatchlist(db);

        var linked = await controller.UpdateTeamAsync(legacy.Id, new WatchlistController.UpdateTeamRequest(EspnId: "360", LeagueSlug: "eng.1"), CancellationToken.None);
        var dto = Assert.IsType<WatchlistController.TeamDto>(Assert.IsType<OkObjectResult>(linked).Value);
        Assert.Equal(("360", "eng.1", (int?)33), (dto.EspnId, dto.LeagueSlug, dto.ApiFootballId));

        var takenEspn = await controller.UpdateTeamAsync(legacy.Id, new WatchlistController.UpdateTeamRequest(EspnId: "359"), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(takenEspn);

        var takenApiFootball = await controller.UpdateTeamAsync(legacy.Id, new WatchlistController.UpdateTeamRequest(ApiFootballId: 42), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(takenApiFootball);

        var cleared = await controller.UpdateTeamAsync(legacy.Id, new WatchlistController.UpdateTeamRequest(ApiFootballId: 0), CancellationToken.None);
        Assert.Null(Assert.IsType<WatchlistController.TeamDto>(Assert.IsType<OkObjectResult>(cleared).Value).ApiFootballId);
    }
}
