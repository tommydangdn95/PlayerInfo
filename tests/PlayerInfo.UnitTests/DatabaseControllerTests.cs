using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Controllers;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class DatabaseControllerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task RequiresLogin()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/database")).StatusCode);

    [Fact]
    public async Task InMemory_ReportsRowCountsAndNewsGrowth_WithoutSizes()
    {
        await using var db = TestDbFactory.CreateInMemory();
        var now = DateTime.UtcNow;
        db.NewsArticles.AddRange(
            new NewsArticle { UrlHash = "1", Url = "https://x/1", Title = "a", FetchedAtUtc = now.AddDays(-1) },
            new NewsArticle { UrlHash = "2", Url = "https://x/2", Title = "b", FetchedAtUtc = now.AddDays(-2) },
            new NewsArticle { UrlHash = "3", Url = "https://x/3", Title = "c", FetchedAtUtc = now.AddDays(-20) });
        db.Teams.Add(new Team { EspnId = "359", Name = "Arsenal" });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:StorageLimitMb"] = "512" }).Build();
        var controller = new DatabaseController(db, Options.Create(new CollectorOptions { ArticleRetentionDays = 60 }), config);

        var info = Assert.IsType<DatabaseController.DatabaseDto>(Assert.IsType<OkObjectResult>(await controller.GetAsync(default)).Value);

        Assert.Equal(("InMemory", (long?)null, 512L * 1024 * 1024, 60, 20), (info.Provider, info.SizeBytes, info.LimitBytes, info.ArticleRetentionDays, info.ApiCallRetentionDays));
        Assert.Equal(3, info.Tables.Single(t => t.Name == "NewsArticles").Rows);
        Assert.Equal(1, info.Tables.Single(t => t.Name == "Teams").Rows);
        Assert.Equal((2, 2.0 / 7), (info.Growth.ArticlesLast7Days, info.Growth.ArticlesPerDay), new ApproxComparer());
        Assert.Null(info.Growth.ProjectedTotalBytes);
    }

    private sealed class ApproxComparer : IEqualityComparer<(int, double)>
    {
        public bool Equals((int, double) x, (int, double) y) => x.Item1 == y.Item1 && Math.Abs(x.Item2 - y.Item2) < 0.06;
        public int GetHashCode((int, double) obj) => obj.Item1;
    }
}
