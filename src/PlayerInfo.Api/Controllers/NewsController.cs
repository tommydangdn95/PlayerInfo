using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class NewsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetArticlesAsync(
        [FromQuery] string? category = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.NewsArticles
            .AsNoTracking()
            .Where(x => x.IsRelevant);

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(x => x.Category == category);
        }

        var items = await query
            .OrderByDescending(x => x.PublishedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.SummaryVi,
                x.Category,
                x.SourceName,
                x.Url,
                x.PublishedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new { page, pageSize, items });
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
}
