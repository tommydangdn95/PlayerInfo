using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Services;

namespace PlayerInfo.UnitTests;

public sealed class QuotaManagerTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task CanConsumeAsync_ReturnsFalse_WhenDailyLimitReached()
    {
        await using var db = CreateInMemoryDb();
        var manager = new QuotaManager(db, NullLogger<QuotaManager>.Instance);

        Assert.True(await manager.CanConsumeAsync("ApiFootball", 2));

        await manager.IncrementAsync("ApiFootball", 2);

        Assert.False(await manager.CanConsumeAsync("ApiFootball", 2));
        Assert.Equal(2, await manager.GetTodayUsageAsync("ApiFootball"));
    }

    [Fact]
    public async Task IncrementAsync_AccumulatesWithinSameDay()
    {
        await using var db = CreateInMemoryDb();
        var manager = new QuotaManager(db, NullLogger<QuotaManager>.Instance);

        await manager.IncrementAsync("NewsData", 1);
        await manager.IncrementAsync("NewsData", 3);

        Assert.Equal(4, await manager.GetTodayUsageAsync("NewsData"));
    }
}
