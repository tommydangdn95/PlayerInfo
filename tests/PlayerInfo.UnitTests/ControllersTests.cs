using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Controllers;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class ControllersTests
{
    [Fact]
    public async Task JobsController_Run_ReturnsUnauthorized_WhenSecretMismatch()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var queue = new JobQueue();
        var quota = new QuotaManager(db, NullLogger<QuotaManager>.Instance);
        var controller = new JobsController(queue, quota, Options.Create(new CronOptions { SecretKey = "my-secret" }));

        var result = await controller.RunAsync("news", "wrong-secret");

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task JobsController_Run_ReturnsBadRequest_WhenModeInvalid()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var queue = new JobQueue();
        var quota = new QuotaManager(db, NullLogger<QuotaManager>.Instance);
        var controller = new JobsController(queue, quota, Options.Create(new CronOptions { SecretKey = "my-secret" }));

        var result = await controller.RunAsync("invalid_mode", "my-secret");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("transfers_news")]
    [InlineData("news")]
    [InlineData("roster")]
    public async Task JobsController_Run_ReturnsAccepted_ForValidModes(string mode)
    {
        await using var db = TestDbFactory.CreateInMemory();
        var queue = new JobQueue();
        var quota = new QuotaManager(db, NullLogger<QuotaManager>.Instance);
        var controller = new JobsController(queue, quota, Options.Create(new CronOptions { SecretKey = "my-secret" }));

        var result = await controller.RunAsync(mode, "my-secret");

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
        var controller = new WatchlistController(db);

        var created = await controller.AddAliasAsync(new WatchlistController.CreateAliasRequest("Team", 1, "Arsenal", "en"), CancellationToken.None);
        var createdResult = Assert.IsType<CreatedResult>(created);
        var alias = Assert.IsType<WatchlistAlias>(createdResult.Value);

        var deleteOk = await controller.DeleteAliasAsync(alias.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteOk);

        var deleteMissing = await controller.DeleteAliasAsync(999, CancellationToken.None);
        Assert.IsType<NotFoundResult>(deleteMissing);
    }
}
