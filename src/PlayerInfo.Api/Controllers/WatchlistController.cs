using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Controllers;

/// <summary>
/// Admin management of tracked teams and their search keywords. Requires an admin JWT (fallback policy).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class WatchlistController(AppDbContext db) : ControllerBase
{
    private static readonly string[] AllowedLanguages = ["en", "vi"];

    public sealed record CreateTeamRequest(
        int ExternalApiId,
        string Name,
        string? Country = null,
        int? LeagueExternalId = null,
        string? LogoUrl = null);

    /// <summary>Partial update: only non-null fields are changed.</summary>
    public sealed record UpdateTeamRequest(
        string? Name = null,
        string? Country = null,
        int? LeagueExternalId = null,
        bool? IsTracked = null);

    /// <param name="EntityType">"Team" or "Player".</param>
    /// <param name="EntityId">Internal <c>Teams.Id</c> / <c>Players.Id</c> (not the API-Football id).</param>
    public sealed record CreateAliasRequest(string EntityType, int EntityId, string Keyword, string Language = "en");

    public sealed record AliasDto(int Id, string Keyword, string Language);

    public sealed record TeamDto(
        int Id,
        int ExternalApiId,
        string Name,
        string? Country,
        string? LogoUrl,
        int? LeagueExternalId,
        bool IsTracked,
        DateTime UpdatedAtUtc,
        IReadOnlyList<AliasDto> Aliases);

    public sealed record TeamSearchResult(
        int ExternalApiId,
        string Name,
        string? Country,
        string? LogoUrl,
        int? Founded,
        bool National,
        int? ExistingTeamId);

    // ---------------------------------------------------------------- Teams

    [HttpGet("teams")]
    public async Task<IActionResult> GetTeamsAsync(CancellationToken ct)
    {
        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var aliases = await db.WatchlistAliases.AsNoTracking()
            .Where(a => a.EntityType == "Team")
            .OrderBy(a => a.Keyword)
            .ToListAsync(ct);
        var aliasesByTeam = aliases.ToLookup(a => a.EntityId);

        return Ok(teams.Select(t => ToDto(t, aliasesByTeam[t.Id])).ToList());
    }

    [HttpGet("teams/{id:int}")]
    public async Task<IActionResult> GetTeamAsync(int id, CancellationToken ct)
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null)
        {
            return NotFound();
        }

        var aliases = await db.WatchlistAliases.AsNoTracking()
            .Where(a => a.EntityType == "Team" && a.EntityId == id)
            .OrderBy(a => a.Keyword)
            .ToListAsync(ct);
        return Ok(ToDto(team, aliases));
    }

    /// <summary>Searches API-Football for teams to add (uses 1 API-Football request from the daily quota).</summary>
    [HttpGet("search-teams")]
    public async Task<IActionResult> SearchTeamsAsync(
        [FromQuery] string? q,
        [FromServices] ApiFootballClient apiFootball,
        CancellationToken ct)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length < 3 || !query.All(c => char.IsLetterOrDigit(c) || c == ' '))
        {
            return BadRequest(new { error = "Nhập ít nhất 3 ký tự (chỉ chữ, số và khoảng trắng)." });
        }

        var found = await apiFootball.SearchTeamsAsync(query, ct);
        var externalIds = found.Select(f => f.Team.Id).ToList();
        var existing = await db.Teams.AsNoTracking()
            .Where(t => externalIds.Contains(t.ExternalApiId))
            .ToDictionaryAsync(t => t.ExternalApiId, t => t.Id, ct);

        return Ok(found
            .Select(f => new TeamSearchResult(
                f.Team.Id,
                f.Team.Name,
                f.Team.Country,
                f.Team.Logo,
                f.Team.Founded,
                f.Team.National,
                existing.TryGetValue(f.Team.Id, out var teamId) ? teamId : null))
            .ToList());
    }

    [HttpGet("leagues")]
    public IActionResult GetTrackedLeagues([FromServices] IOptions<CollectorOptions> options)
        => Ok(options.Value.TrackedLeagueIds);

    [HttpPost("teams")]
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
            Country = string.IsNullOrWhiteSpace(req.Country) ? null : req.Country.Trim(),
            LogoUrl = string.IsNullOrWhiteSpace(req.LogoUrl) ? null : req.LogoUrl.Trim(),
            LeagueExternalId = req.LeagueExternalId,
            IsTracked = true
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return Created($"/api/watchlist/teams/{team.Id}", ToDto(team, []));
    }

    [HttpPatch("teams/{id:int}")]
    public async Task<IActionResult> UpdateTeamAsync(int id, [FromBody] UpdateTeamRequest req, CancellationToken ct)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null)
        {
            return NotFound();
        }

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return BadRequest(new { error = "name cannot be empty." });
            }

            team.Name = req.Name.Trim();
        }

        if (req.Country is not null)
        {
            team.Country = string.IsNullOrWhiteSpace(req.Country) ? null : req.Country.Trim();
        }

        if (req.LeagueExternalId is not null)
        {
            team.LeagueExternalId = req.LeagueExternalId > 0 ? req.LeagueExternalId : null;
        }

        if (req.IsTracked is not null)
        {
            team.IsTracked = req.IsTracked.Value;
        }

        team.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var aliases = await db.WatchlistAliases.AsNoTracking()
            .Where(a => a.EntityType == "Team" && a.EntityId == id)
            .OrderBy(a => a.Keyword)
            .ToListAsync(ct);
        return Ok(ToDto(team, aliases));
    }

    /// <summary>Deletes the team with its keywords and article links. Players keep existing (TeamId set to null).</summary>
    [HttpDelete("teams/{id:int}")]
    public async Task<IActionResult> DeleteTeamAsync(int id, CancellationToken ct)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null)
        {
            return NotFound();
        }

        db.WatchlistAliases.RemoveRange(
            await db.WatchlistAliases.Where(a => a.EntityType == "Team" && a.EntityId == id).ToListAsync(ct));
        db.ArticleEntities.RemoveRange(
            await db.ArticleEntities.Where(a => a.EntityType == "Team" && a.EntityId == id).ToListAsync(ct));
        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- Aliases

    [HttpGet("aliases")]
    public async Task<IActionResult> GetAliasesAsync(CancellationToken ct)
        => Ok(await db.WatchlistAliases.AsNoTracking().OrderBy(a => a.Keyword).ToListAsync(ct));

    [HttpPost("aliases")]
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

    private static TeamDto ToDto(Team t, IEnumerable<WatchlistAlias> aliases) => new(
        t.Id,
        t.ExternalApiId,
        t.Name,
        t.Country,
        t.LogoUrl,
        t.LeagueExternalId,
        t.IsTracked,
        t.UpdatedAtUtc,
        aliases.Select(a => new AliasDto(a.Id, a.Keyword, a.Language)).ToList());

    private static string? NormalizeEntityType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "team" => "Team",
        "player" => "Player",
        _ => null
    };
}
