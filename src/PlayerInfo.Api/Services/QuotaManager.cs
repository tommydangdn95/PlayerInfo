using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Services;

/// <summary>
/// Tracks daily API request counts in ApiQuotaLogs to protect free-tier quotas.
/// Uses its own short-lived DbContext per call so that recording quota never flushes
/// (or gets blocked by) unrelated pending changes of the collector pipeline.
/// </summary>
public sealed class QuotaManager(IDbContextFactory<AppDbContext> dbFactory, ILogger<QuotaManager> logger)
{
    public const string ApiFootballProvider = "ApiFootball";
    public const string NewsDataProvider = "NewsData";

    public async Task<bool> CanConsumeAsync(string provider, int dailyLimit, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var current = await GetTodayUsageAsync(provider, ct);

        if (current < dailyLimit)
        {
            return true;
        }

        logger.LogWarning("Quota limit reached for {Provider}: {Current}/{Limit} on {Date}", provider, current, dailyLimit, today);
        return false;
    }

    public async Task IncrementAsync(string provider, int count = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var row = await db.ApiQuotaLogs
            .FirstOrDefaultAsync(x => x.Provider == provider && x.LogDate == today, ct);

        if (row is null)
        {
            db.ApiQuotaLogs.Add(new ApiQuotaLog
            {
                Provider = provider,
                LogDate = today,
                RequestCount = count
            });
        }
        else
        {
            row.RequestCount += count;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<int> GetTodayUsageAsync(string provider, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.ApiQuotaLogs
            .AsNoTracking()
            .Where(x => x.Provider == provider && x.LogDate == today)
            .Select(x => x.RequestCount)
            .FirstOrDefaultAsync(ct);
    }
}
