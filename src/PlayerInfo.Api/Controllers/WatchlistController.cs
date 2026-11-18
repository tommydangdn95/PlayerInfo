using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Security;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class WatchlistController(AppDbContext db) : ControllerBase
{
    private static readonly string[] AllowedLanguages = ["en", "vi"];

    public sealed record CreateTeamRequest(int ExternalApiId, string Name, string? Country = null, int? LeagueExternalId = null);

    /// <param name="EntityType">"Team" or "Player".</param>
    /// <param name="EntityId">Internal <c>Teams.Id</c> / <c>Players.Id</c> (not the API-Football id).</param>
    public sealed record CreateAliasRequest(string EntityType, int EntityId, string Keyword, string Language = "en");

    [HttpGet("teams")]
    public async Task<IActionResult> GetTeamsAsync(CancellationToken ct)
        => Ok(await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct));

    [HttpPost("teams")]
    [RequireCronSecret]
    public async Task<IActionResult> AddTeamAsync([FromBody] CreateTeamRequest req, CancellationToken ct)
    {
        if (req.ExternalApiId <= 0 || string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "externalApiId must be > 0 and name is required." });
        }

        if (await db.Teams.AnyAsync(t => t.ExternalApiId == req.ExternalApiId, ct))
        {
            return Conflict(new { error = $"Team with externalApiId={req.ExternalApiId} already exists." });
        }

        var team = new Team
        {
            ExternalApiId = req.ExternalApiId,
            Name = req.Name.Trim(),
            Country = req.Country?.Trim(),
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
    [RequireCronSecret]
    public async Task<IActionResult> AddAliasAsync([FromBody] CreateAliasRequest req, CancellationToken ct)
    {
        var entityType = NormalizeEntityType(req.EntityType);
        if (entityType is null)
        {
            return BadRequest(new { error = "entityType must be 'Team' or 'Player'." });
        }

        var keyword = req.Keyword?.Trim();
        if (string.IsNullOrEmpty(keyword) || keyword.Length > 200)
        {
            return BadRequest(new { error = "keyword is required (max 200 characters)." });
        }

        var language = (req.Language ?? "en").Trim().ToLowerInvariant();
        if (!AllowedLanguages.Contains(language))
        {
            return BadRequest(new { error = "language must be 'en' or 'vi'." });
        }

        var entityExists = entityType == "Team"
            ? await db.Teams.AnyAsync(t => t.Id == req.EntityId, ct)
            : await db.Players.AnyAsync(p => p.Id == req.EntityId, ct);
        if (!entityExists)
        {
            return BadRequest(new { error = $"{entityType} with id={req.EntityId} does not exist." });
        }

        var duplicate = await db.WatchlistAliases.AnyAsync(a =>
            a.EntityType == entityType &&
            a.EntityId == req.EntityId &&
            a.Language == language &&
            a.Keyword.ToLower() == keyword.ToLower(), ct);
        if (duplicate)
        {
            return Conflict(new { error = "This alias already exists." });
        }

        var alias = new WatchlistAlias
        {
            EntityType = entityType,
            EntityId = req.EntityId,
            Keyword = keyword,
            Language = language
        };
        db.WatchlistAliases.Add(alias);
        await db.SaveChangesAsync(ct);
        return Created($"/api/watchlist/aliases/{alias.Id}", alias);
    }

    [HttpDelete("aliases/{id:int}")]
    [RequireCronSecret]
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

    private static string? NormalizeEntityType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "team" => "Team",
        "player" => "Player",
        _ => null
    };
}
