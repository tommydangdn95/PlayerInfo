using PlayerInfo.UnitTests.Helpers;

namespace PlayerInfo.UnitTests;

public sealed class QuotaManagerTests
{
    [Fact]
    public async Task CanConsumeAsync_ReturnsFalse_WhenDailyLimitReached()
    {
        var manager = TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString());

        Assert.True(await manager.CanConsumeAsync("ApiFootball", 2));

        await manager.IncrementAsync("ApiFootball", 2);

        Assert.False(await manager.CanConsumeAsync("ApiFootball", 2));
        Assert.Equal(2, await manager.GetTodayUsageAsync("ApiFootball"));
    }

    [Fact]
    public async Task IncrementAsync_AccumulatesWithinSameDay()
    {
        var manager = TestDbFactory.CreateQuotaManager(Guid.NewGuid().ToString());

        await manager.IncrementAsync("NewsData", 1);
        await manager.IncrementAsync("NewsData", 3);

        Assert.Equal(4, await manager.GetTodayUsageAsync("NewsData"));
    }

    [Fact]
    public async Task IncrementAsync_DoesNotFlushPendingChangesOfOtherContexts()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var pipelineDb = TestDbFactory.CreateInMemory(dbName);
        pipelineDb.Teams.Add(new PlayerInfo.Api.Data.Entities.Team { EspnId = "1", Name = "Pending" });

        await TestDbFactory.CreateQuotaManager(dbName).IncrementAsync("ApiFootball");

        await using var reader = TestDbFactory.CreateInMemory(dbName);
        Assert.Empty(reader.Teams);
    }
}
