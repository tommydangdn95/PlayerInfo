using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class NewsController(AppDbContext db) : ControllerBase
{
    public sealed record ArticleTeamDto(int Id, string Name, string? LogoUrl);

    public sealed record ArticleDto(
        long Id,
        string Title,
        string? SummaryVi,
        string Category,
        string SourceName,
        string Url,
        DateTime PublishedAtUtc,
        bool IsHot,
        int Importance,
        string? ClaimType,
        int SourceTier,
        long? StoryId,
        IReadOnlyList<ArticleTeamDto> Teams);

    /// <summary>
    /// Relevant articles, newest first. Each article carries the teams it is about: teams linked directly,
    /// plus the teams of linked players. Filters: category, team (directly or through its players),
    /// a published-at range [fromUtc, toUtc) and hot (Gemini-flagged major news) only.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetArticlesAsync(
        [FromQuery] string? category = null,
        [FromQuery] int? teamId = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] bool hot = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = Filter(category, fromUtc, toUtc, hot);
        if (teamId is { } tid)
        {
            var playerIds = db.Players.Where(p => p.TeamId == tid).Select(p => p.Id);
            query = query.Where(x => x.Entities.Any(e =>
                (e.EntityType == "Team" && e.EntityId == tid) ||
                (e.EntityType == "Player" && playerIds.Contains(e.EntityId))));
        }

        var total = await query.CountAsync(ct);
        var ids = await query
            .OrderByDescending(x => x.PublishedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var items = await LoadArticlesAsync(ids, ct);
        return Ok(new { page, pageSize, total, items });
    }

    [HttpGet("injuries")]
    public async Task<IActionResult> GetInjuriesAsync([FromQuery] int limit = 30, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var items = await db.PlayerInjuries
            .AsNoTracking()
            .OrderByDescending(x => x.ReportedDateUtc)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("transfers")]
    public async Task<IActionResult> GetTransfersAsync([FromQuery] int limit = 30, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var items = await db.PlayerTransfers
            .AsNoTracking()
            .OrderByDescending(x => x.TransferDateUtc)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(items);
    }

    private IQueryable<NewsArticle> Filter(string? category, DateTime? fromUtc, DateTime? toUtc, bool hot)
    {
        var query = db.NewsArticles.AsNoTracking().Where(x => x.IsRelevant);

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(x => x.Category == category);
        }

        if (fromUtc is { } from)
        {
            var f = AsUtc(from);
            query = query.Where(x => x.PublishedAtUtc >= f);
        }

        if (toUtc is { } to)
        {
            var t = AsUtc(to);
            query = query.Where(x => x.PublishedAtUtc < t);
        }

        if (hot)
        {
            query = query.Where(x => x.IsHot);
        }

        return query;
    }

    /// <summary>The given articles as DTOs with their teams, newest first.</summary>
    private async Task<List<ArticleDto>> LoadArticlesAsync(List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.NewsArticles.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Title, x.SummaryVi, x.Category, x.SourceName, x.Url, x.PublishedAtUtc, x.IsHot, x.Importance, x.ClaimType, x.SourceTier, x.StoryId })
            .ToListAsync(ct);

        var teamsByArticle = await LoadArticleTeamsAsync(ids, ct);
        return rows
            .OrderByDescending(r => r.PublishedAtUtc)
            .ThenByDescending(r => r.Id)
            .Select(r => new ArticleDto(r.Id, r.Title, r.SummaryVi, r.Category, r.SourceName, r.Url, r.PublishedAtUtc, r.IsHot,
                r.Importance, r.ClaimType, r.SourceTier, r.StoryId,
                teamsByArticle.TryGetValue(r.Id, out var teams) ? teams : []))
            .ToList();
    }

    private async Task<Dictionary<long, IReadOnlyList<ArticleTeamDto>>> LoadArticleTeamsAsync(List<long> articleIds, CancellationToken ct)
    {
        var links = await db.ArticleEntities
            .AsNoTracking()
            .Where(e => articleIds.Contains(e.ArticleId))
            .Select(e => new { e.ArticleId, e.EntityType, e.EntityId })
            .ToListAsync(ct);

        var linkedPlayerIds = links.Where(l => l.EntityType == "Player").Select(l => l.EntityId).Distinct().ToList();
        var playerTeams = linkedPlayerIds.Count == 0
            ? new Dictionary<int, int>()
            : await db.Players.AsNoTracking()
                .Where(p => linkedPlayerIds.Contains(p.Id) && p.TeamId != null)
                .ToDictionaryAsync(p => p.Id, p => p.TeamId!.Value, ct);

        var pairs = links
            .Select(l => (l.ArticleId, TeamId: l.EntityType == "Team"
                ? l.EntityId
                : playerTeams.TryGetValue(l.EntityId, out var t) ? t : (int?)null))
            .Where(p => p.TeamId != null)
            .Select(p => (p.ArticleId, TeamId: p.TeamId!.Value))
            .Distinct()
            .ToList();

        var teamIds = pairs.Select(p => p.TeamId).Distinct().ToList();
        var teams = await db.Teams.AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => new ArticleTeamDto(t.Id, t.Name, t.LogoUrl), ct);

        return pairs
            .Where(p => teams.ContainsKey(p.TeamId))
            .GroupBy(p => p.ArticleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ArticleTeamDto>)g.Select(p => teams[p.TeamId]).OrderBy(t => t.Name).ToList());
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
