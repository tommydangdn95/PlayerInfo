using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class WatchlistController(AppDbContext db) : ControllerBase
{
    public sealed record CreateTeamRequest(int ExternalApiId, string Name, string? Country = null, int? LeagueExternalId = null);
    public sealed record CreateAliasRequest(string EntityType, int EntityId, string Keyword, string Language = "en");

    [HttpGet("teams")]
    public async Task<IActionResult> GetTeamsAsync(CancellationToken ct)
        => Ok(await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct));

    [HttpPost("teams")]
    public async Task<IActionResult> AddTeamAsync([FromBody] CreateTeamRequest req, CancellationToken ct)
    {
        var team = new Team
        {
            ExternalApiId = req.ExternalApiId,
            Name = req.Name.Trim(),
            Country = req.Country,
            LeagueExternalId = req.LeagueExternalId,
            IsTracked = true
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return Created($"/api/watchlist/teams/{team.Id}", team);
    }

    [HttpGet("aliases")]
    public async Task<IActionResult> GetAliasesAsync(CancellationToken ct)
        => Ok(await db.WatchlistAliases.AsNoTracking().OrderBy(a => a.Keyword).ToListAsync(ct));

    [HttpPost("aliases")]
    public async Task<IActionResult> AddAliasAsync([FromBody] CreateAliasRequest req, CancellationToken ct)
    {
        var alias = new WatchlistAlias
        {
            EntityType = req.EntityType,
            EntityId = req.EntityId,
            Keyword = req.Keyword.Trim(),
            Language = req.Language.Trim().ToLowerInvariant()
        };
        db.WatchlistAliases.Add(alias);
        await db.SaveChangesAsync(ct);
        return Created($"/api/watchlist/aliases/{alias.Id}", alias);
    }

    [HttpDelete("aliases/{id:int}")]
    public async Task<IActionResult> DeleteAliasAsync(int id, CancellationToken ct)
    {
        var alias = await db.WatchlistAliases.FindAsync([id], ct);
        if (alias is null)
        {
            return NotFound();
        }

        db.WatchlistAliases.Remove(alias);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
