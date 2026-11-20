using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

/// <summary>Source credibility rules ("Nguồn tin"). Every change is re-applied to the last 30 days of articles.</summary>
[ApiController]
[Route("api/[controller]")]
public sealed class SourcesController(AppDbContext db) : ControllerBase
{
    public static readonly TimeSpan ReapplyWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan UnclassifiedWindow = TimeSpan.FromDays(7);

    public sealed record SourceRequest(string Pattern, int Tier);

    public sealed record SourceDto(int Id, string Pattern, int Tier);

    public sealed record UnclassifiedSourceDto(string SourceName, int ArticleCount);

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
        => Ok(await db.NewsSources.AsNoTracking()
            .OrderBy(s => s.Tier == SourceTiers.Junk ? 9 : s.Tier)
            .ThenBy(s => s.Pattern)
            .Select(s => new SourceDto(s.Id, s.Pattern, s.Tier))
            .ToListAsync(ct));

    /// <summary>Sources of the last 7 days that no rule matches (so they count as tier 3), most frequent first.</summary>
    [HttpGet("unclassified")]
    public async Task<IActionResult> GetUnclassifiedAsync(CancellationToken ct)
    {
        var matcher = await SourceTiers.LoadMatcherAsync(db, ct);
        var since = DateTime.UtcNow - UnclassifiedWindow;
        var counts = await db.NewsArticles.AsNoTracking()
            .Where(a => a.FetchedAtUtc >= since)
            .GroupBy(a => a.SourceName)
            .Select(g => new { SourceName = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return Ok(counts
            .Where(c => !string.IsNullOrWhiteSpace(c.SourceName) && matcher.MatchRule(c.SourceName, null) is null)
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.SourceName)
            .Take(50)
            .Select(c => new UnclassifiedSourceDto(c.SourceName, c.Count))
            .ToList());
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] SourceRequest req, CancellationToken ct)
    {
        var (pattern, error) = await ValidateAsync(req, null, ct);
        if (error is not null)
        {
            return error;
        }

        var source = new NewsSource { Pattern = pattern, Tier = req.Tier };
        db.NewsSources.Add(source);
        await db.SaveChangesAsync(ct);
        var reapplied = await SourceTiers.ReapplyAsync(db, ReapplyWindow, ct);
        return Created($"/api/sources/{source.Id}", new { source = new SourceDto(source.Id, source.Pattern, source.Tier), reapplied });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] SourceRequest req, CancellationToken ct)
    {
        var source = await db.NewsSources.FindAsync([id], ct);
        if (source is null)
        {
            return NotFound();
        }

        var (pattern, error) = await ValidateAsync(req, id, ct);
        if (error is not null)
        {
            return error;
        }

        source.Pattern = pattern;
        source.Tier = req.Tier;
        await db.SaveChangesAsync(ct);
        var reapplied = await SourceTiers.ReapplyAsync(db, ReapplyWindow, ct);
        return Ok(new { source = new SourceDto(source.Id, source.Pattern, source.Tier), reapplied });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken ct)
    {
        var source = await db.NewsSources.FindAsync([id], ct);
        if (source is null)
        {
            return NotFound();
        }

        db.NewsSources.Remove(source);
        await db.SaveChangesAsync(ct);
        var reapplied = await SourceTiers.ReapplyAsync(db, ReapplyWindow, ct);
        return Ok(new { reapplied });
    }

    private async Task<(string Pattern, IActionResult? Error)> ValidateAsync(SourceRequest req, int? id, CancellationToken ct)
    {
        var pattern = req.Pattern?.Trim() ?? string.Empty;
        if (pattern.Length is < 2 or > 100)
        {
            return (pattern, BadRequest(new { error = "Tên/tên miền nguồn phải dài 2-100 ký tự." }));
        }

        if (req.Tier is < 0 or > 3)
        {
            return (pattern, BadRequest(new { error = "Hạng phải từ 0 (rác) đến 3." }));
        }

        var lower = pattern.ToLower();
        if (await db.NewsSources.AnyAsync(s => s.Id != id && s.Pattern.ToLower() == lower, ct))
        {
            return (pattern, Conflict(new { error = "Nguồn này đã có trong danh sách." }));
        }

        return (pattern, null);
    }
}
