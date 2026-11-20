using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

/// <summary>
/// Stories (the same news reported by several sources, grouped by Gemini) per team and day, and the team
/// briefing: stories plus match, injury and transfer data.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class StoriesController(AppDbContext db) : ControllerBase
{
    public const int MaxArticlesPerStory = 15;
    public const int MaxPendingArticles = 20;
    public static readonly TimeSpan InjuryWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan TransferWindow = TimeSpan.FromDays(90);

    public sealed record StoryDto(
        long Id,
        string HeadlineVi,
        string? SummaryVi,
        string Topic,
        int Importance,
        bool IsHot,
        string Confidence,
        int SourceCount,
        int BestTier,
        DateTime FirstSeenUtc,
        DateTime LastUpdatedUtc);

    public sealed record ArticleRefDto(long Id, string Title, string SourceName, int SourceTier, string? ClaimType, string Url, DateTime PublishedAtUtc);

    public sealed record BriefingStoryDto(StoryDto Story, int ArticleCount, IReadOnlyList<ArticleRefDto> Articles);

    public sealed record TeamStoriesDto(
        int Id,
        string Name,
        string? LogoUrl,
        string? LeagueSlug,
        int StoryCount,
        int HotCount,
        int PendingCount,
        IReadOnlyList<StoryDto> Stories);

    public sealed record TeamInfoDto(int Id, string Name, string? LogoUrl, string? LeagueSlug);

    public sealed record MatchDto(DateTime KickoffUtc, string League, string Home, string Away, int? HomeGoals, int? AwayGoals, string Status);

    public sealed record InjuryDto(string PlayerName, string Reason, string? Status, DateTime ReportedDateUtc);

    public sealed record TransferDto(string PlayerName, string From, string To, string? Type, DateTime DateUtc, bool Incoming);

    public sealed record BriefingDto(
        TeamInfoDto Team,
        string Day,
        MatchDto? LastMatch,
        MatchDto? NextMatch,
        IReadOnlyList<InjuryDto> Injuries,
        IReadOnlyList<TransferDto> Transfers,
        IReadOnlyList<BriefingStoryDto> Stories,
        int PendingCount,
        IReadOnlyList<ArticleRefDto> PendingArticles);

    /// <summary>One entry per tracked team (or only <paramref name="teamId"/>): the day's most important stories.</summary>
    [HttpGet("by-team")]
    public async Task<IActionResult> GetByTeamAsync(
        [FromQuery] string? day = null,
        [FromQuery] int? teamId = null,
        [FromQuery] bool hot = false,
        [FromQuery] int perTeam = 3,
        CancellationToken ct = default)
    {
        perTeam = Math.Clamp(perTeam, 1, 20);
        var (from, to) = VnDay.UtcRange(VnDay.ParseOrToday(day));

        var teams = await db.Teams.AsNoTracking()
            .Where(t => t.IsTracked && (teamId == null || t.Id == teamId))
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var links = await LoadTeamArticleLinksAsync(teams.Select(t => t.Id).ToList(), from, to, ct);
        var storyIds = links.Where(l => l.StoryId != null).Select(l => l.StoryId!.Value).Distinct().ToList();
        var stories = await db.Stories.AsNoTracking().Where(s => storyIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        var result = teams.Select(t =>
        {
            var teamLinks = links.Where(l => l.TeamId == t.Id).ToList();
            var teamStories = teamLinks
                .Where(l => l.StoryId != null && stories.ContainsKey(l.StoryId.Value))
                .Select(l => stories[l.StoryId!.Value])
                .DistinctBy(s => s.Id)
                .Where(s => !hot || s.Importance >= NewsTaxonomy.HotImportance)
                .OrderByDescending(s => s.Importance)
                .ThenByDescending(s => s.SourceCount)
                .ThenByDescending(s => s.LastUpdatedUtc)
                .ToList();

            return new TeamStoriesDto(
                t.Id, t.Name, t.LogoUrl, t.LeagueSlug,
                teamStories.Count,
                teamStories.Count(s => s.Importance >= NewsTaxonomy.HotImportance),
                teamLinks.Where(l => l.StoryId == null).Select(l => l.ArticleId).Distinct().Count(),
                teamStories.Take(perTeam).Select(ToDto).ToList());
        }).ToList();

        return Ok(result);
    }

    /// <summary>Everything about one team for one Vietnam day, ready to be shared with an audience.</summary>
    [HttpGet("briefing/{teamId:int}")]
    public async Task<IActionResult> GetBriefingAsync(int teamId, [FromQuery] string? day = null, CancellationToken ct = default)
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null)
        {
            return NotFound();
        }

        var date = VnDay.ParseOrToday(day);
        var (from, to) = VnDay.UtcRange(date);
        var links = await LoadTeamArticleLinksAsync([teamId], from, to, ct);

        // Stories, most important first, each with its sources (most reliable first).
        var storyIds = links.Where(l => l.StoryId != null).Select(l => l.StoryId!.Value).Distinct().ToList();
        var stories = await db.Stories.AsNoTracking()
            .Where(s => storyIds.Contains(s.Id))
            .OrderByDescending(s => s.Importance)
            .ThenByDescending(s => s.SourceCount)
            .ThenByDescending(s => s.LastUpdatedUtc)
            .ToListAsync(ct);
        var storyArticles = (await db.NewsArticles.AsNoTracking()
                .Where(a => a.StoryId != null && storyIds.Contains(a.StoryId.Value) && a.IsRelevant)
                .Select(a => new { StoryId = a.StoryId!.Value, Ref = new ArticleRefDto(a.Id, a.Title, a.SourceName, a.SourceTier, a.ClaimType, a.Url, a.PublishedAtUtc) })
                .ToListAsync(ct))
            .ToLookup(x => x.StoryId, x => x.Ref);

        var briefingStories = stories.Select(s =>
        {
            var refs = storyArticles[s.Id].ToList();
            return new BriefingStoryDto(ToDto(s), refs.Count, OrderSources(refs).Take(MaxArticlesPerStory).ToList());
        }).ToList();

        // Relevant articles of the day not (yet) grouped into a story, e.g. while Gemini is unavailable.
        var pendingIds = links.Where(l => l.StoryId == null).Select(l => l.ArticleId).Distinct().ToList();
        var pending = await db.NewsArticles.AsNoTracking()
            .Where(a => pendingIds.Contains(a.Id))
            .Select(a => new ArticleRefDto(a.Id, a.Title, a.SourceName, a.SourceTier, a.ClaimType, a.Url, a.PublishedAtUtc))
            .ToListAsync(ct);

        var (lastMatch, nextMatch) = await LoadMatchesAsync(team, ct);

        return Ok(new BriefingDto(
            new TeamInfoDto(team.Id, team.Name, team.LogoUrl, team.LeagueSlug),
            date.ToString("yyyy-MM-dd"),
            lastMatch,
            nextMatch,
            await LoadInjuriesAsync(team, ct),
            await LoadTransfersAsync(team, ct),
            briefingStories,
            pending.Count,
            OrderSources(pending).Take(MaxPendingArticles).ToList()));
    }

    private sealed record ArticleTeamLink(long ArticleId, long? StoryId, int TeamId);

    /// <summary>Relevant articles published in [from, to) about the teams (directly or through their players).</summary>
    private async Task<List<ArticleTeamLink>> LoadTeamArticleLinksAsync(List<int> teamIds, DateTime from, DateTime to, CancellationToken ct)
    {
        if (teamIds.Count == 0)
        {
            return [];
        }

        var playerTeam = await db.Players.AsNoTracking()
            .Where(p => p.TeamId != null && teamIds.Contains(p.TeamId.Value))
            .ToDictionaryAsync(p => p.Id, p => p.TeamId!.Value, ct);
        var playerIds = playerTeam.Keys.ToList();

        var rows = await db.NewsArticles.AsNoTracking()
            .Where(a => a.IsRelevant && a.PublishedAtUtc >= from && a.PublishedAtUtc < to)
            .SelectMany(a => a.Entities, (a, e) => new { a.Id, a.StoryId, e.EntityType, e.EntityId })
            .Where(x => (x.EntityType == "Team" && teamIds.Contains(x.EntityId)) ||
                        (x.EntityType == "Player" && playerIds.Contains(x.EntityId)))
            .ToListAsync(ct);

        return rows
            .Select(r => new ArticleTeamLink(r.Id, r.StoryId, r.EntityType == "Team" ? r.EntityId : playerTeam[r.EntityId]))
            .Distinct()
            .ToList();
    }

    private async Task<(MatchDto? Last, MatchDto? Next)> LoadMatchesAsync(Team team, CancellationToken ct)
    {
        if (team.EspnId is null)
        {
            return (null, null);
        }

        var now = DateTime.UtcNow;
        var teamFixtures = db.Fixtures.AsNoTracking().Where(f => f.HomeTeamEspnId == team.EspnId || f.AwayTeamEspnId == team.EspnId);
        var last = await teamFixtures
            .Where(f => f.KickoffAtUtc <= now && f.HomeGoals != null && f.AwayGoals != null)
            .OrderByDescending(f => f.KickoffAtUtc)
            .FirstOrDefaultAsync(ct);
        var next = await teamFixtures
            .Where(f => f.KickoffAtUtc > now)
            .OrderBy(f => f.KickoffAtUtc)
            .FirstOrDefaultAsync(ct);

        return (ToMatch(last), ToMatch(next));
    }

    private async Task<List<InjuryDto>> LoadInjuriesAsync(Team team, CancellationToken ct)
    {
        if (team.EspnId is null)
        {
            return [];
        }

        var since = DateTime.UtcNow - InjuryWindow;
        var rows = await db.PlayerInjuries.AsNoTracking()
            .Where(i => i.TeamEspnId == team.EspnId && i.ReportedDateUtc >= since)
            .OrderByDescending(i => i.ReportedDateUtc)
            .ToListAsync(ct);

        // Latest report per player.
        return rows
            .DistinctBy(i => i.PlayerEspnId)
            .Select(i => new InjuryDto(i.PlayerName, i.Reason, i.Type, i.ReportedDateUtc))
            .ToList();
    }

    private async Task<List<TransferDto>> LoadTransfersAsync(Team team, CancellationToken ct)
    {
        if (team.ApiFootballId is not { } id)
        {
            return [];
        }

        var since = DateTime.UtcNow - TransferWindow;
        return await db.PlayerTransfers.AsNoTracking()
            .Where(t => (t.FromTeamExternalId == id || t.ToTeamExternalId == id) && t.TransferDateUtc >= since)
            .OrderByDescending(t => t.TransferDateUtc)
            .Take(10)
            .Select(t => new TransferDto(t.PlayerName, t.FromTeamName, t.ToTeamName, t.TransferType, t.TransferDateUtc, t.ToTeamExternalId == id))
            .ToListAsync(ct);
    }

    private static IEnumerable<ArticleRefDto> OrderSources(IEnumerable<ArticleRefDto> refs)
        => refs.OrderBy(r => r.SourceTier == SourceTiers.Junk ? 9 : r.SourceTier).ThenByDescending(r => r.PublishedAtUtc);

    private static MatchDto? ToMatch(Fixture? f)
        => f is null ? null : new MatchDto(f.KickoffAtUtc, f.LeagueName, f.HomeTeamName, f.AwayTeamName, f.HomeGoals, f.AwayGoals, f.StatusShort);

    internal static StoryDto ToDto(Story s) => new(
        s.Id, s.HeadlineVi, s.SummaryVi, s.Topic, s.Importance, s.Importance >= NewsTaxonomy.HotImportance,
        s.Confidence, s.SourceCount, s.BestTier, s.FirstSeenUtc, s.LastUpdatedUtc);
}
