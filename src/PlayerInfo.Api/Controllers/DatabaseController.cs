using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

/// <summary>
/// Database health for the admin "Cơ sở dữ liệu" page: size against the Neon free-tier limit, per-table rows and
/// sizes (read-only PostgreSQL statistics), news growth and the size it should settle at under the retention rules.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class DatabaseController(
    AppDbContext db,
    IOptions<CollectorOptions> options,
    IConfiguration configuration,
    ILogger<DatabaseController>? logger = null) : ControllerBase
{
    /// <summary>Neon free tier storage, overridable with Database__StorageLimitMb.</summary>
    public const int DefaultStorageLimitMb = 1024;

    /// <summary>Tables that hold the news (their size grows with articles and shrinks with the retention).</summary>
    private static readonly string[] NewsTables = ["NewsArticles", "ArticleEntities", "Stories"];

    public sealed record TableDto(
        string Name, long Rows, long? DeadRows, long? TotalBytes, long? TableBytes, long? IndexBytes,
        DateTime? LastVacuumUtc, DateTime? LastAnalyzeUtc);

    public sealed record GrowthDto(
        int ArticlesLast7Days,
        double ArticlesPerDay,
        long? BytesPerArticle,
        long? ProjectedNewsBytes,
        long? ProjectedTotalBytes,
        DateTime? OldestArticleUtc,
        DateTime? NewestArticleUtc);

    public sealed record DatabaseDto(
        string Provider,
        string? Version,
        string? DatabaseName,
        string? Host,
        long? SizeBytes,
        long LimitBytes,
        int ArticleRetentionDays,
        int ApiCallRetentionDays,
        IReadOnlyList<string> AppliedMigrations,
        IReadOnlyList<string> PendingMigrations,
        IReadOnlyList<TableDto> Tables,
        GrowthDto Growth,
        string? Error = null);

    // Raw-SQL row shapes (column aliases match property names).
    private sealed record TableStat(
        string Name, long Rows, long DeadRows, long TotalBytes, long TableBytes, long IndexBytes,
        DateTime? LastVacuum, DateTime? LastAnalyze);

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
    {
        var limitMb = configuration.GetValue("Database:StorageLimitMb", DefaultStorageLimitMb);
        var retentionDays = options.Value.ArticleRetentionDays;

        // Growth from the data itself (works on every provider).
        var since = DateTime.UtcNow.AddDays(-7);
        var last7 = await db.NewsArticles.AsNoTracking().CountAsync(a => a.FetchedAtUtc >= since, ct);
        var oldest = await db.NewsArticles.AsNoTracking().OrderBy(a => a.FetchedAtUtc).Select(a => (DateTime?)a.FetchedAtUtc).FirstOrDefaultAsync(ct);
        var newest = await db.NewsArticles.AsNoTracking().OrderByDescending(a => a.FetchedAtUtc).Select(a => (DateTime?)a.FetchedAtUtc).FirstOrDefaultAsync(ct);
        // Average over the days actually covered, so a young database is not underestimated.
        var days = oldest is null ? 7 : Math.Clamp((DateTime.UtcNow - oldest.Value).TotalDays, 1, 7);
        var perDay = last7 / days;

        if (!db.Database.IsRelational())
        {
            // Local development (InMemory): row counts only, no sizes.
            return Ok(new DatabaseDto(
                "InMemory", null, null, null, null, limitMb * 1024L * 1024, retentionDays, (int)ApiUsageRecorder.Retention.TotalDays,
                [], [], await CountTablesAsync(ct), new GrowthDto(last7, Math.Round(perDay, 1), null, null, null, oldest, newest)));
        }

        try
        {
            return Ok(await PostgresInfoAsync(limitMb, retentionDays, last7, perDay, oldest, newest, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Statistics unavailable (permissions, provider quirk): still show row counts.
            logger?.LogWarning(ex, "Could not read PostgreSQL statistics");
            return Ok(new DatabaseDto(
                "PostgreSQL", null, null, null, null, limitMb * 1024L * 1024, retentionDays, (int)ApiUsageRecorder.Retention.TotalDays,
                [], [], await CountTablesAsync(ct), new GrowthDto(last7, Math.Round(perDay, 1), null, null, null, oldest, newest),
                $"Không đọc được thống kê PostgreSQL: {ex.Message}"));
        }
    }

    private async Task<List<TableDto>> CountTablesAsync(CancellationToken ct)
    {
        var tables = new List<TableDto>
            {
                await CountAsync("NewsArticles", db.NewsArticles, ct),
                await CountAsync("ArticleEntities", db.ArticleEntities, ct),
                await CountAsync("Stories", db.Stories, ct),
                await CountAsync("ApiCalls", db.ApiCalls, ct),
                await CountAsync("Teams", db.Teams, ct),
                await CountAsync("Players", db.Players, ct),
                await CountAsync("WatchlistAliases", db.WatchlistAliases, ct),
                await CountAsync("NewsSources", db.NewsSources, ct),
                await CountAsync("LeagueTeams", db.LeagueTeams, ct),
                await CountAsync("Fixtures", db.Fixtures, ct),
                await CountAsync("PlayerInjuries", db.PlayerInjuries, ct),
                await CountAsync("PlayerTransfers", db.PlayerTransfers, ct),
                await CountAsync("ApiQuotaLogs", db.ApiQuotaLogs, ct)
            };
        return tables.OrderByDescending(t => t.Rows).ToList();
    }

    private async Task<DatabaseDto> PostgresInfoAsync(
        int limitMb, int retentionDays, int last7, double perDay, DateTime? oldest, DateTime? newest, CancellationToken ct)
    {
        var version = await Scalar<string>("SELECT version() AS \"Value\"", ct);
        var size = await Scalar<long>("SELECT pg_database_size(current_database()) AS \"Value\"", ct);
        var stats = await db.Database.SqlQueryRaw<TableStat>("""
            SELECT relname AS "Name",
                   n_live_tup AS "Rows",
                   n_dead_tup AS "DeadRows",
                   pg_total_relation_size(relid) AS "TotalBytes",
                   pg_relation_size(relid) AS "TableBytes",
                   pg_indexes_size(relid) AS "IndexBytes",
                   GREATEST(last_vacuum, last_autovacuum) AS "LastVacuum",
                   GREATEST(last_analyze, last_autoanalyze) AS "LastAnalyze"
            FROM pg_stat_user_tables
            ORDER BY pg_total_relation_size(relid) DESC
            """).ToListAsync(ct);

        var tableDtos = stats
            .Select(s => new TableDto(s.Name, s.Rows, s.DeadRows, s.TotalBytes, s.TableBytes, s.IndexBytes,
                AsUtc(s.LastVacuum), AsUtc(s.LastAnalyze)))
            .ToList();

        // Exact article count (pg statistics are estimates) to size one article with its links and story share.
        var articles = await db.NewsArticles.AsNoTracking().LongCountAsync(ct);
        var newsBytes = stats.Where(s => NewsTables.Contains(s.Name)).Sum(s => s.TotalBytes);
        long? bytesPerArticle = articles > 0 ? newsBytes / articles : null;
        long? projectedNews = bytesPerArticle is { } b && retentionDays > 0 ? (long)(perDay * retentionDays * b) : null;
        long? projectedTotal = projectedNews is { } p ? size - newsBytes + p : null;

        var connection = db.Database.GetDbConnection();
        return new DatabaseDto(
            "PostgreSQL",
            version?.Split(" on ")[0],
            connection.Database,
            HostOf(connection),
            size,
            limitMb * 1024L * 1024,
            retentionDays,
            (int)ApiUsageRecorder.Retention.TotalDays,
            (await db.Database.GetAppliedMigrationsAsync(ct)).ToList(),
            (await db.Database.GetPendingMigrationsAsync(ct)).ToList(),
            tableDtos,
            new GrowthDto(last7, Math.Round(perDay, 1), bytesPerArticle, projectedNews, projectedTotal, oldest, newest));
    }

    private static async Task<TableDto> CountAsync<T>(string name, IQueryable<T> set, CancellationToken ct)
        => new(name, await set.LongCountAsync(ct), null, null, null, null, null, null);

    private async Task<T?> Scalar<T>(string sql, CancellationToken ct)
        => await db.Database.SqlQueryRaw<T>(sql).FirstOrDefaultAsync(ct);

    private static DateTime? AsUtc(DateTime? value)
        => value is { } v ? (v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime()) : null;

    /// <summary>Server host only: never the user name or password.</summary>
    private static string? HostOf(DbConnection connection)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };
            return builder.TryGetValue("Host", out var host) ? host?.ToString() : connection.DataSource;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
