using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Services;

/// <summary>
/// Tracks daily API request counts in ApiQuotaLogs to protect free-tier quotas.
/// </summary>
public sealed class QuotaManager(AppDbContext db, ILogger<QuotaManager> logger)
{
    public async Task<bool> CanConsumeAsync(string provider, int dailyLimit, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var current = await db.ApiQuotaLogs
            .AsNoTracking()
            .Where(x => x.Provider == provider && x.LogDate == today)
            .Select(x => x.RequestCount)
            .FirstOrDefaultAsync(ct);

        if (current < dailyLimit)
        {
            return true;
        }

        logger.LogWarning("Quota limit reached for {Provider}: {Current}/{Limit} on {Date}", provider, current, dailyLimit, today);
        return false;
    }

    public async Task IncrementAsync(string provider, int count = 1, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var row = await db.ApiQuotaLogs
            .FirstOrDefaultAsync(x => x.Provider == provider && x.LogDate == today, ct);

        if (row is null)
        {
            row = new ApiQuotaLog
            {
                Provider = provider,
                LogDate = today,
                RequestCount = count
            };
            db.ApiQuotaLogs.Add(row);
        }
        else
        {
            row.RequestCount += count;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<int> GetTodayUsageAsync(string provider, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.ApiQuotaLogs
            .AsNoTracking()
            .Where(x => x.Provider == provider && x.LogDate == today)
            .Select(x => x.RequestCount)
            .FirstOrDefaultAsync(ct);
    }
}
