using Microsoft.AspNetCore.Mvc;
using PlayerInfo.Api.Controllers;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class NewsTaxonomyTests
{
    private static NewsArticle A(string source, int tier, string? claim) => new() { SourceName = source, SourceTier = tier, ClaimType = claim, IsRelevant = true };

    [Fact]
    public void Confidence_OfficialNeedsAReliableSource()
    {
        Assert.Equal("Official", NewsTaxonomy.ComputeConfidence([A("BBC", 1, "Official")]));
        Assert.Equal("Rumour", NewsTaxonomy.ComputeConfidence([A("The Sun", 3, "Official")])); // a tabloid saying "official" is not enough
    }

    [Fact]
    public void Confidence_ConfirmedByTier1Report_OrTwoReliableSources()
    {
        Assert.Equal("Confirmed", NewsTaxonomy.ComputeConfidence([A("ESPN", 1, "Report")]));
        Assert.Equal("Confirmed", NewsTaxonomy.ComputeConfidence([A("Goal", 2, "Report"), A("Marca", 2, "Report")]));
        Assert.Equal("Rumour", NewsTaxonomy.ComputeConfidence([A("Goal", 2, "Report"), A("goal", 2, "Report")])); // same source twice
        Assert.Equal("Rumour", NewsTaxonomy.ComputeConfidence([A("ESPN", 1, "Rumour"), A("Mirror", 3, "Report")]));
    }

    [Fact]
    public void Confidence_OnlyOpinionPieces_IsOpinion()
        => Assert.Equal("Opinion", NewsTaxonomy.ComputeConfidence([A("Guardian", 1, "Opinion"), A("Mirror", 3, "Opinion")]));

    [Fact]
    public void Rescore_CountsDistinctRelevantSources_AndBestTier()
    {
        var story = new Story();
        var junk = A("FanDuel", 0, "Report");
        junk.IsRelevant = false;
        NewsTaxonomy.Rescore(story, [A("BBC Sport", 1, "Report"), A("bbc sport", 1, "Report"), A("Mirror", 3, "Rumour"), junk]);

        Assert.Equal((2, 1, "Confirmed"), (story.SourceCount, story.BestTier, story.Confidence));
    }
}

public sealed class SourceTiersTests
{
    [Fact]
    public void Matcher_LongestPatternWins_MatchesHost_AndDefaultsToTier3()
    {
        var matcher = new SourceTiers.Matcher([("Mail", 2), ("Daily Mail", 3), ("bbc.co.uk", 1)]);

        Assert.Equal(3, matcher.Match("Daily Mail Online", null));
        Assert.Equal(2, matcher.Match("Mail & Guardian", null));
        Assert.Equal(1, matcher.Match("Some feed", "https://www.bbc.co.uk/sport/1"));
        Assert.Equal(3, matcher.Match("Unknown Blog", "https://blog.example"));
        Assert.Null(matcher.MatchRule("Unknown Blog", null));
    }

    [Fact]
    public async Task EnsureDefaults_SeedsOnce()
    {
        await using var db = TestDbFactory.CreateInMemory();
        await SourceTiers.EnsureDefaultsAsync(db);
        await SourceTiers.EnsureDefaultsAsync(db);
        Assert.Equal(SourceTiers.Defaults.Count, db.NewsSources.Count());
    }
}

public sealed class SourcesControllerTests
{
    [Fact]
    public async Task MarkingASourceAsJunk_HidesItsRecentArticles_AndUndoingGivesThemBackToGemini()
    {
        await using var db = TestDbFactory.CreateInMemory();
        db.NewsArticles.Add(new NewsArticle { UrlHash = "a", Url = "https://tips.example/1", Title = "Tips", SourceName = "Tipsy Bets", IsAiProcessed = true });
        await db.SaveChangesAsync();
        var controller = new SourcesController(db);

        var created = Assert.IsType<CreatedResult>(await controller.CreateAsync(new("Tipsy", 0), default));
        var article = db.NewsArticles.Single();
        Assert.Equal((0, false), (article.SourceTier, article.IsRelevant));

        var id = db.NewsSources.Single().Id;
        Assert.IsType<OkObjectResult>(await controller.UpdateAsync(id, new("Tipsy", 2), default));
        Assert.Equal((2, true, false), (article.SourceTier, article.IsRelevant, article.IsAiProcessed));
        Assert.NotNull(created.Value);
    }

    [Theory]
    [InlineData("x", 1)]
    [InlineData("Valid", 4)]
    public async Task Create_ValidatesInput(string pattern, int tier)
    {
        await using var db = TestDbFactory.CreateInMemory();
        Assert.IsType<BadRequestObjectResult>(await new SourcesController(db).CreateAsync(new(pattern, tier), default));
    }

    [Fact]
    public async Task Create_RejectsDuplicates_AndUnclassifiedListsUnmatchedSources()
    {
        await using var db = TestDbFactory.CreateInMemory();
        db.NewsSources.Add(new NewsSource { Pattern = "BBC", Tier = 1 });
        db.NewsArticles.AddRange(
            new NewsArticle { UrlHash = "1", Url = "https://x/1", Title = "a", SourceName = "BBC Sport" },
            new NewsArticle { UrlHash = "2", Url = "https://x/2", Title = "b", SourceName = "Blog FC" },
            new NewsArticle { UrlHash = "3", Url = "https://x/3", Title = "c", SourceName = "Blog FC" });
        await db.SaveChangesAsync();
        var controller = new SourcesController(db);

        Assert.IsType<ConflictObjectResult>(await controller.CreateAsync(new("bbc", 2), default));

        var list = Assert.IsType<List<SourcesController.UnclassifiedSourceDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetUnclassifiedAsync(default)).Value);
        Assert.Equal(new SourcesController.UnclassifiedSourceDto("Blog FC", 2), Assert.Single(list));
    }
}

public sealed class StoriesControllerTests
{
    private static async Task<(AppDbContext Db, Team Arsenal, Team Chelsea, Story Hot, Story Minor)> SeedAsync()
    {
        var db = TestDbFactory.CreateInMemory();
        var arsenal = new Team { EspnId = "359", Name = "Arsenal", LogoUrl = "https://logo/359.png", ApiFootballId = 42 };
        var chelsea = new Team { EspnId = "363", Name = "Chelsea" };
        db.Teams.AddRange(arsenal, chelsea);
        await db.SaveChangesAsync();
        var saka = new Player { EspnId = "1001", FullName = "Bukayo Saka", TeamId = arsenal.Id };
        db.Players.Add(saka);

        var hot = new Story { HeadlineVi = "Saka chấn thương", Topic = "Injury", Importance = 5, Confidence = "Official", SourceCount = 2 };
        var minor = new Story { HeadlineVi = "Ảnh tập", Topic = "Other", Importance = 1, SourceCount = 1 };
        db.Stories.AddRange(hot, minor);
        await db.SaveChangesAsync();

        var (from, _) = VnDay.UtcRange(VnDay.Today());
        var a1 = new NewsArticle { UrlHash = "1", Url = "https://x/1", Title = "Saka out", SourceName = "Mirror", SourceTier = 3, StoryId = hot.Id, PublishedAtUtc = from.AddHours(2) };
        var a2 = new NewsArticle { UrlHash = "2", Url = "https://x/2", Title = "Saka injury confirmed", SourceName = "BBC", SourceTier = 1, StoryId = hot.Id, PublishedAtUtc = from.AddHours(3) };
        var a3 = new NewsArticle { UrlHash = "3", Url = "https://x/3", Title = "Training", SourceName = "Blog", StoryId = minor.Id, PublishedAtUtc = from.AddHours(4) };
        var pending = new NewsArticle { UrlHash = "4", Url = "https://x/4", Title = "Not yet processed", SourceName = "ESPN", SourceTier = 1, PublishedAtUtc = from.AddHours(5) };
        var yesterday = new NewsArticle { UrlHash = "5", Url = "https://x/5", Title = "Old", SourceName = "BBC", PublishedAtUtc = from.AddHours(-2) };
        db.NewsArticles.AddRange(a1, a2, a3, pending, yesterday);
        await db.SaveChangesAsync();
        db.ArticleEntities.AddRange(
            new ArticleEntity { ArticleId = a1.Id, EntityType = "Player", EntityId = saka.Id },
            new ArticleEntity { ArticleId = a2.Id, EntityType = "Team", EntityId = arsenal.Id },
            new ArticleEntity { ArticleId = a3.Id, EntityType = "Team", EntityId = arsenal.Id },
            new ArticleEntity { ArticleId = pending.Id, EntityType = "Team", EntityId = arsenal.Id },
            new ArticleEntity { ArticleId = yesterday.Id, EntityType = "Team", EntityId = chelsea.Id });

        db.PlayerInjuries.AddRange(
            new PlayerInjury { PlayerEspnId = "1001", PlayerName = "Bukayo Saka", TeamEspnId = "359", Reason = "Hamstring", Type = "Out", ReportedDateUtc = DateTime.UtcNow.AddDays(-1) },
            new PlayerInjury { PlayerEspnId = "1001", PlayerName = "Bukayo Saka", TeamEspnId = "359", Reason = "Knock", ReportedDateUtc = DateTime.UtcNow.AddDays(-10) });
        db.Fixtures.AddRange(
            new Fixture { EspnEventId = "1", LeagueName = "Premier League", HomeTeamEspnId = "359", HomeTeamName = "Arsenal", AwayTeamEspnId = "349", AwayTeamName = "Bournemouth", HomeGoals = 2, AwayGoals = 1, KickoffAtUtc = DateTime.UtcNow.AddDays(-2) },
            new Fixture { EspnEventId = "2", LeagueName = "Premier League", HomeTeamEspnId = "363", HomeTeamName = "Chelsea", AwayTeamEspnId = "359", AwayTeamName = "Arsenal", KickoffAtUtc = DateTime.UtcNow.AddDays(3) });
        db.PlayerTransfers.Add(new PlayerTransfer { PlayerName = "X", FromTeamExternalId = 47, FromTeamName = "Tottenham", ToTeamExternalId = 42, ToTeamName = "Arsenal", TransferDateUtc = DateTime.UtcNow.AddDays(-5) });
        await db.SaveChangesAsync();
        return (db, arsenal, chelsea, hot, minor);
    }

    [Fact]
    public async Task ByTeam_ReturnsTheDaysStoriesPerTeam_MostImportantFirst()
    {
        var (db, arsenal, chelsea, hot, _) = await SeedAsync();
        await using var _db = db;
        var controller = new StoriesController(db);

        var teams = Assert.IsType<List<StoriesController.TeamStoriesDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetByTeamAsync()).Value);

        Assert.Equal(["Arsenal", "Chelsea"], teams.Select(t => t.Name));
        var a = teams[0];
        Assert.Equal((2, 1, 1), (a.StoryCount, a.HotCount, a.PendingCount));
        Assert.Equal(["Saka chấn thương", "Ảnh tập"], a.Stories.Select(s => s.HeadlineVi));
        Assert.True(a.Stories[0].IsHot);
        Assert.Equal(0, teams[1].StoryCount); // its only article was yesterday

        var hotOnly = Assert.IsType<List<StoriesController.TeamStoriesDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetByTeamAsync(teamId: arsenal.Id, hot: true)).Value);
        Assert.Equal(hot.Id, Assert.Single(Assert.Single(hotOnly).Stories).Id);

        var yesterday = VnDay.Today().AddDays(-1).ToString("yyyy-MM-dd");
        var past = Assert.IsType<List<StoriesController.TeamStoriesDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetByTeamAsync(day: yesterday, teamId: chelsea.Id)).Value);
        Assert.Equal(1, Assert.Single(past).PendingCount);
    }

    [Fact]
    public async Task Briefing_CombinesStoriesWithSources_MatchesInjuriesAndTransfers()
    {
        var (db, arsenal, _, _, _) = await SeedAsync();
        await using var _db = db;

        var result = await new StoriesController(db).GetBriefingAsync(arsenal.Id);
        var b = Assert.IsType<StoriesController.BriefingDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(("Arsenal", "https://logo/359.png"), (b.Team.Name, b.Team.LogoUrl));
        Assert.Equal(["Saka chấn thương", "Ảnh tập"], b.Stories.Select(s => s.Story.HeadlineVi));
        Assert.Equal(["BBC", "Mirror"], b.Stories[0].Articles.Select(a => a.SourceName)); // most reliable first
        Assert.Equal(2, b.Stories[0].ArticleCount);
        Assert.Equal("Not yet processed", Assert.Single(b.PendingArticles).Title);
        Assert.Equal((2, 1), (b.LastMatch!.HomeGoals, b.LastMatch.AwayGoals));
        Assert.Equal("Chelsea", b.NextMatch!.Home);
        Assert.Equal("Hamstring", Assert.Single(b.Injuries).Reason); // latest report per player
        Assert.True(Assert.Single(b.Transfers).Incoming);
    }

    [Fact]
    public async Task Briefing_UnknownTeam_IsNotFound()
    {
        await using var db = TestDbFactory.CreateInMemory();
        Assert.IsType<NotFoundResult>(await new StoriesController(db).GetBriefingAsync(99));
    }
}
