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
public sealed class WatchlistController(AppDbContext db, IOptions<CollectorOptions> collectorOptions) : ControllerBase
{
    private static readonly string[] AllowedLanguages = ["en", "vi"];

    /// <param name="EspnId">ESPN team id (from GET league-teams).</param>
    /// <param name="LeagueSlug">ESPN league slug, one of GET leagues.</param>
    /// <param name="ApiFootballId">Optional API-Football team id, only needed for transfers.</param>
    public sealed record CreateTeamRequest(
        string EspnId,
        string LeagueSlug,
        string Name,
        string? Country = null,
        string? LogoUrl = null,
        int? ApiFootballId = null);

    /// <summary>
    /// Partial update: only non-null fields change. <c>EspnId</c> + <c>LeagueSlug</c> link a team to ESPN;
    /// <c>ApiFootballId = 0</c> clears the API-Football link.
    /// </summary>
    public sealed record UpdateTeamRequest(
        string? Name = null,
        string? Country = null,
        string? EspnId = null,
        string? LeagueSlug = null,
        int? ApiFootballId = null,
        bool? IsTracked = null);

    /// <param name="EntityType">"Team" or "Player".</param>
    /// <param name="EntityId">Internal <c>Teams.Id</c> / <c>Players.Id</c> (not an ESPN or API-Football id).</param>
    public sealed record CreateAliasRequest(string EntityType, int EntityId, string Keyword, string Language = "en");

    public sealed record AliasDto(int Id, string Keyword, string Language);

    public sealed record TeamDto(
        int Id,
        string? EspnId,
        string? LeagueSlug,
        int? ApiFootballId,
        string Name,
        string? Country,
        string? LogoUrl,
        bool IsTracked,
        DateTime UpdatedAtUtc,
        IReadOnlyList<AliasDto> Aliases);

    public sealed record LeagueTeamResult(string EspnId, string Name, string? ShortName, string? LogoUrl, int? ExistingTeamId);

    public sealed record ApiFootballTeamResult(
        int ApiFootballId,
        string Name,
        string? Country,
        string? LogoUrl,
        int? Founded,
        bool National,
        int? LinkedTeamId);

    // ---------------------------------------------------------------- Leagues & lookups

    /// <summary>Configured ESPN leagues (Collector:Leagues).</summary>
    [HttpGet("leagues")]
    public IActionResult GetLeagues() => Ok(collectorOptions.Value.Leagues);

    /// <summary>All teams of a configured league from ESPN (no key, no quota), marking those already added.</summary>
    [HttpGet("league-teams")]
    public async Task<IActionResult> GetLeagueTeamsAsync(
        [FromQuery] string? league,
        [FromServices] EspnClient espn,
        CancellationToken ct)
    {
        var slug = FindLeague(league)?.Slug;
        if (slug is null)
        {
            return BadRequest(new { error = "Giải đấu không hợp lệ." });
        }

        var teams = await espn.GetLeagueTeamsAsync(slug, ct);
        var espnIds = teams.Select(t => t.Id).ToList();
        var existing = await db.Teams.AsNoTracking()
            .Where(t => t.EspnId != null && espnIds.Contains(t.EspnId))
            .ToDictionaryAsync(t => t.EspnId!, t => t.Id, ct);

        return Ok(teams
            .Select(t => new LeagueTeamResult(t.Id, t.Name, t.ShortName, t.LogoUrl, existing.TryGetValue(t.Id, out var id) ? id : null))
            .ToList());
    }

    /// <summary>
    /// Searches API-Football teams by name to find a team's API-Football id (used only for transfers).
    /// Costs 1 API-Football request from the daily quota.
    /// </summary>
    [HttpGet("search-teams")]
    public async Task<IActionResult> SearchApiFootballTeamsAsync(
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
        var ids = found.Select(f => (int?)f.Team.Id).ToList();
        var linked = await db.Teams.AsNoTracking()
            .Where(t => ids.Contains(t.ApiFootballId))
            .ToDictionaryAsync(t => t.ApiFootballId!.Value, t => t.Id, ct);

        return Ok(found
            .Select(f => new ApiFootballTeamResult(
                f.Team.Id,
                f.Team.Name,
                f.Team.Country,
                f.Team.Logo,
                f.Team.Founded,
                f.Team.National,
                linked.TryGetValue(f.Team.Id, out var teamId) ? teamId : null))
            .ToList());
    }

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

        return Ok(ToDto(team, await GetTeamAliasesAsync(id, ct)));
    }

    [HttpPost("teams")]
    public async Task<IActionResult> AddTeamAsync([FromBody] CreateTeamRequest req, CancellationToken ct)
    {
        var espnId = req.EspnId?.Trim();
        var league = FindLeague(req.LeagueSlug);
        if (!IsValidEspnId(espnId) || league is null || string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "espnId (số), leagueSlug (một trong các giải đã cấu hình) và name là bắt buộc." });
        }

        if (req.ApiFootballId is <= 0)
        {
            return BadRequest(new { error = "apiFootballId phải > 0." });
        }

        if (await db.Teams.AnyAsync(t => t.EspnId == espnId, ct))
        {
            return Conflict(new { error = $"Đội có ESPN id {espnId} đã tồn tại." });
        }

        if (req.ApiFootballId is { } afId && await db.Teams.AnyAsync(t => t.ApiFootballId == afId, ct))
        {
            return Conflict(new { error = $"API-Football id {afId} đã gắn với đội khác." });
        }

        var team = new Team
        {
            EspnId = espnId,
            LeagueSlug = league.Slug,
            ApiFootballId = req.ApiFootballId,
            Name = req.Name.Trim(),
            Country = string.IsNullOrWhiteSpace(req.Country) ? null : req.Country.Trim(),
            LogoUrl = string.IsNullOrWhiteSpace(req.LogoUrl) ? null : req.LogoUrl.Trim(),
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

        if (req.LeagueSlug is not null)
        {
            var league = FindLeague(req.LeagueSlug);
            if (league is null)
            {
                return BadRequest(new { error = "Giải đấu không hợp lệ." });
            }

            team.LeagueSlug = league.Slug;
        }

        if (req.EspnId is not null)
        {
            var espnId = req.EspnId.Trim();
            if (!IsValidEspnId(espnId))
            {
                return BadRequest(new { error = "espnId phải là số." });
            }

            if (await db.Teams.AnyAsync(t => t.Id != id && t.EspnId == espnId, ct))
            {
                return Conflict(new { error = $"ESPN id {espnId} đã gắn với đội khác." });
            }

            team.EspnId = espnId;
        }

        if (req.ApiFootballId is not null)
        {
            if (req.ApiFootballId < 0)
            {
                return BadRequest(new { error = "apiFootballId phải >= 0 (0 để bỏ liên kết)." });
            }

            if (req.ApiFootballId > 0 && await db.Teams.AnyAsync(t => t.Id != id && t.ApiFootballId == req.ApiFootballId, ct))
            {
                return Conflict(new { error = $"API-Football id {req.ApiFootballId} đã gắn với đội khác." });
            }

            team.ApiFootballId = req.ApiFootballId > 0 ? req.ApiFootballId : null;
        }

        if (req.IsTracked is not null)
        {
            team.IsTracked = req.IsTracked.Value;
        }

        if (team.EspnId is not null && team.LeagueSlug is null)
        {
            return BadRequest(new { error = "Cần chọn giải đấu khi liên kết ESPN." });
        }

        team.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(team, await GetTeamAliasesAsync(id, ct)));
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
        t.EspnId,
        t.LeagueSlug,
        t.ApiFootballId,
        t.Name,
        t.Country,
        t.LogoUrl,
        t.IsTracked,
        t.UpdatedAtUtc,
        aliases.Select(a => new AliasDto(a.Id, a.Keyword, a.Language)).ToList());

    private Task<List<WatchlistAlias>> GetTeamAliasesAsync(int teamId, CancellationToken ct)
        => db.WatchlistAliases.AsNoTracking()
            .Where(a => a.EntityType == "Team" && a.EntityId == teamId)
            .OrderBy(a => a.Keyword)
            .ToListAsync(ct);

    private LeagueOption? FindLeague(string? slug)
        => string.IsNullOrWhiteSpace(slug)
            ? null
            : collectorOptions.Value.Leagues.FirstOrDefault(l => string.Equals(l.Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsValidEspnId(string? espnId)
        => !string.IsNullOrEmpty(espnId) && espnId.Length <= 20 && espnId.All(char.IsAsciiDigit);

    private static string? NormalizeEntityType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "team" => "Team",
        "player" => "Player",
        _ => null
    };
}
