using Microsoft.AspNetCore.Mvc;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Controllers;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class ControllersTests
{
    [Fact]
    public async Task JobsController_Run_ReturnsBadRequest_WhenModeInvalid()
    {
        var controller = new JobsController(new JobQueue(), TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString()));

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
        var controller = new JobsController(new JobQueue(), TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString()));

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
    public async Task WatchlistController_CrudAliases_WorksAndReturnsNotFoundForMissingId()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var team = new Team { ExternalApiId = 42, Name = "Arsenal" };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        var controller = new WatchlistController(db);

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
        db.Teams.Add(new Team { Id = 1, ExternalApiId = 42, Name = "Arsenal" });
        await db.SaveChangesAsync();
        var controller = new WatchlistController(db);

        var result = await controller.AddAliasAsync(new WatchlistController.CreateAliasRequest(type, entityId, keyword, language), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task WatchlistController_AddTeam_ReturnsConflict_ForDuplicateExternalId()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var controller = new WatchlistController(db);

        var first = await controller.AddTeamAsync(new WatchlistController.CreateTeamRequest(33, "Manchester United"), CancellationToken.None);
        var second = await controller.AddTeamAsync(new WatchlistController.CreateTeamRequest(33, "Man Utd"), CancellationToken.None);

        Assert.IsType<CreatedResult>(first);
        Assert.IsType<ConflictObjectResult>(second);
    }
}
