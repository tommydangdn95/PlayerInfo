using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Services;

public enum CollectorMode
{
    Full,
    TransfersNews,
    News,
    Roster
}

/// <summary>
/// Orchestrates football data sync, news ingestion, deduplication, AI enrichment, and notifications.
/// Each step runs in isolation: a failing step is logged and the remaining steps still run.
/// </summary>
public sealed class CollectorPipeline(
    AppDbContext db,
    ApiFootballClient apiFootball,
    RssNewsClient rssNews,
    NewsDataClient newsData,
    GeminiEnricherClient gemini,
    TelegramNotifier telegram,
    IOptions<CollectorOptions> options,
    ILogger<CollectorPipeline> logger)
{
    /// <summary>Max articles offered to one Telegram digest (fewer are sent if the message would be too long).</summary>
    public const int DigestMaxArticles = 15;

    /// <summary>Articles still not AI-processed after this long are given up on (not retried, not notified).</summary>
    public static readonly TimeSpan PendingAiMaxAge = TimeSpan.FromHours(48);

    /// <summary>How far back stored headlines are compared for title-based deduplication.</summary>
    public static readonly TimeSpan TitleDedupWindow = TimeSpan.FromDays(3);

    private delegate Task Step(CancellationToken ct);

    public async Task RunAsync(CollectorMode mode, CancellationToken ct = default)
    {
        logger.LogInformation("Starting CollectorPipeline in mode={Mode} at {TimeUtc}", mode, DateTime.UtcNow);

        var label = mode.ToString();
        (string Name, Step Run)[] newsSteps =
        [
            ("IngestNews", IngestNewsAsync),
            ("EnrichNews", EnrichPendingArticlesAsync),
            ("NotifyTelegram", c => NotifyAsync(label, c))
        ];

        (string Name, Step Run)[] steps = mode switch
        {
            CollectorMode.Roster => [("SyncRosters", SyncRostersAsync)],
            CollectorMode.Full =>
            [
                ("SyncInjuries", SyncInjuriesAsync),
                ("SyncFixtures", SyncFixturesAsync),
                ("SyncTransfers", SyncTransfersAsync),
                .. newsSteps
            ],
            CollectorMode.TransfersNews => [("SyncTransfers", SyncTransfersAsync), .. newsSteps],
            CollectorMode.News => newsSteps,
            _ => []
        };

        var failed = new List<string>();
        foreach (var (name, run) in steps)
        {
            if (!await RunStepAsync(name, run, ct))
            {
                failed.Add(name);
            }
        }

        if (failed.Count == 0)
        {
            logger.LogInformation("Finished CollectorPipeline in mode={Mode} at {TimeUtc}", mode, DateTime.UtcNow);
        }
        else
        {
            logger.LogWarning("Finished CollectorPipeline in mode={Mode} at {TimeUtc} with failed steps: {FailedSteps}",
                mode, DateTime.UtcNow, string.Join(", ", failed));
        }
    }

    private async Task<bool> RunStepAsync(string name, Step step, CancellationToken ct)
    {
        try
        {
            await step(ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Pipeline step {Step} failed", name);
            // Drop whatever the failed step left pending so it cannot poison the next SaveChanges.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    // ---------------------------------------------------------------- Football data

    private async Task SyncInjuriesAsync(CancellationToken ct)
    {
        var cfg = options.Value;
        foreach (var leagueId in cfg.TrackedLeagueIds)
        {
            var injuries = await apiFootball.GetInjuriesByLeagueAsync(leagueId, cfg.CurrentSeason, ct);
            if (injuries.Count == 0)
            {
                continue;
            }

            var playerIds = injuries.Select(i => i.Player.Id).Distinct().ToList();
            var existing = await db.PlayerInjuries
                .AsNoTracking()
                .Where(x => playerIds.Contains(x.PlayerExternalId))
                .Select(x => new { x.PlayerExternalId, x.ReportedDateUtc })
                .ToListAsync(ct);
            var seen = existing.Select(x => (x.PlayerExternalId, x.ReportedDateUtc)).ToHashSet();

            var added = 0;
            foreach (var item in injuries)
            {
                var reportedDate = DateTime.SpecifyKind(item.Fixture.Date.UtcDateTime.Date, DateTimeKind.Utc);
                if (!seen.Add((item.Player.Id, reportedDate)))
                {
                    continue;
                }

                db.PlayerInjuries.Add(new PlayerInjury
                {
                    PlayerExternalId = item.Player.Id,
                    PlayerName = item.Player.Name,
                    TeamExternalId = item.Team.Id,
                    TeamName = item.Team.Name,
                    Reason = item.Injury?.Reason ?? "Unknown",
                    Type = item.Injury?.Type,
                    ReportedDateUtc = reportedDate
                });
                added++;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("League {LeagueId}: {Added} new injuries", leagueId, added);
        }
    }

    private async Task SyncFixturesAsync(CancellationToken ct)
    {
        var trackedLeagues = options.Value.TrackedLeagueIds.ToHashSet();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var date in new[] { today.AddDays(-1), today })
        {
            var fixtures = (await apiFootball.GetFixturesByDateAsync(date, ct))
                .Where(x => trackedLeagues.Contains(x.League.Id))
                .DistinctBy(x => x.Fixture.Id)
                .ToList();
            if (fixtures.Count == 0)
            {
                continue;
            }

            var ids = fixtures.Select(f => f.Fixture.Id).ToList();
            var existing = await db.Fixtures
                .Where(x => ids.Contains(x.ExternalFixtureId))
                .ToDictionaryAsync(x => x.ExternalFixtureId, ct);

            foreach (var f in fixtures)
            {
                if (existing.TryGetValue(f.Fixture.Id, out var row))
                {
                    row.HomeGoals = f.Goals.Home;
                    row.AwayGoals = f.Goals.Away;
                    row.StatusShort = f.Fixture.Status.Short;
                    row.KickoffAtUtc = f.Fixture.Date.UtcDateTime;
                    row.UpdatedAtUtc = DateTime.UtcNow;
                    continue;
                }

                db.Fixtures.Add(new Fixture
                {
                    ExternalFixtureId = f.Fixture.Id,
                    LeagueExternalId = f.League.Id,
                    LeagueName = f.League.Name,
                    HomeTeamName = f.Teams.Home.Name,
                    AwayTeamName = f.Teams.Away.Name,
                    HomeGoals = f.Goals.Home,
                    AwayGoals = f.Goals.Away,
                    StatusShort = f.Fixture.Status.Short,
                    KickoffAtUtc = f.Fixture.Date.UtcDateTime
                });
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SyncTransfersAsync(CancellationToken ct)
    {
        var trackedTeams = await db.Teams
            .AsNoTracking()
            .Where(t => t.IsTracked && t.ExternalApiId > 0)
            .ToListAsync(ct);

        var cutoff = DateTime.UtcNow.AddDays(-7);
        // Shared across teams: a transfer between two tracked teams shows up in both responses.
        var seen = new HashSet<(int PlayerId, DateTime Date)>();

        foreach (var team in trackedTeams)
        {
            var envelopes = await apiFootball.GetTransfersByTeamAsync(team.ExternalApiId, ct);

            var recent = envelopes
                .SelectMany(env => (env.Transfers ?? []).Select(detail => (env.Player, detail)))
                .Select(x => (x.Player, x.detail, Date: ParseTransferDate(x.detail.Date)))
                .Where(x => x.Date is not null && x.Date >= cutoff)
                .ToList();
            if (recent.Count == 0)
            {
                continue;
            }

            var playerIds = recent.Select(x => x.Player.Id).Distinct().ToList();
            var existing = await db.PlayerTransfers
                .AsNoTracking()
                .Where(x => playerIds.Contains(x.PlayerExternalId))
                .Select(x => new { x.PlayerExternalId, x.TransferDateUtc })
                .ToListAsync(ct);
            seen.UnionWith(existing.Select(x => (x.PlayerExternalId, x.TransferDateUtc)));

            foreach (var (player, detail, date) in recent)
            {
                if (!seen.Add((player.Id, date!.Value)))
                {
                    continue;
                }

                db.PlayerTransfers.Add(new PlayerTransfer
                {
                    PlayerExternalId = player.Id,
                    PlayerName = player.Name,
                    FromTeamExternalId = detail.Teams.Out.Id,
                    FromTeamName = detail.Teams.Out.Name,
                    ToTeamExternalId = detail.Teams.In.Id,
                    ToTeamName = detail.Teams.In.Name,
                    TransferType = detail.Type,
                    TransferDateUtc = date.Value
                });
            }

            await db.SaveChangesAsync(ct);
        }
    }

    internal static DateTime? ParseTransferDate(string? value)
        => DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private async Task SyncRostersAsync(CancellationToken ct)
    {
        var trackedTeams = await db.Teams
            .AsNoTracking()
            .Where(t => t.IsTracked && t.ExternalApiId > 0)
            .ToListAsync(ct);

        foreach (var team in trackedTeams)
        {
            var squadPlayers = (await apiFootball.GetSquadByTeamAsync(team.ExternalApiId, ct))
                .SelectMany(s => s.Players ?? [])
                .DistinctBy(p => p.Id)
                .ToList();
            if (squadPlayers.Count == 0)
            {
                continue;
            }

            var ids = squadPlayers.Select(p => p.Id).ToList();
            var existing = await db.Players
                .Where(x => ids.Contains(x.ExternalApiId))
                .ToDictionaryAsync(x => x.ExternalApiId, ct);

            foreach (var p in squadPlayers)
            {
                if (!existing.TryGetValue(p.Id, out var player))
                {
                    player = new Player { ExternalApiId = p.Id, IsTracked = true };
                    db.Players.Add(player);
                }

                player.TeamId = team.Id;
                player.FullName = p.Name;
                player.Number = p.Number;
                player.Position = p.Position;
                player.PhotoUrl = p.Photo;
                player.UpdatedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
    }

    // ---------------------------------------------------------------- News

    private async Task IngestNewsAsync(CancellationToken ct)
    {
        var aliases = await db.WatchlistAliases.AsNoTracking().ToListAsync(ct);
        var rawItems = new List<RawNewsItem>();

        // 1. Google News RSS grouped by entity & language (with jitter to avoid IP throttling)
        var groupedQueries = aliases
            .GroupBy(a => new { a.EntityType, a.EntityId, a.Language })
            .Select(g => new
            {
                g.Key.Language,
                Query = string.Join(" OR ", g.Select(x => $"\"{x.Keyword}\""))
            })
            .ToList();

        foreach (var q in groupedQueries)
        {
            rawItems.AddRange(await rssNews.FetchGoogleNewsAsync(q.Query, q.Language, ct));
            await Task.Delay(Random.Shared.Next(1200, 2500), ct);
        }

        // 2. Direct Sports RSS feeds filtered locally by tracked keywords
        var allKeywords = aliases.Select(a => a.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var feed in options.Value.SportsRssFeeds)
        {
            var feedItems = await rssNews.FetchSportsFeedAsync(feed, ct);
            rawItems.AddRange(allKeywords.Count == 0
                ? feedItems
                : feedItems.Where(item => allKeywords.Any(kw =>
                    item.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    (item.Snippet?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false))));
        }

        // 3. NewsData.io (optional if key configured): one combined query per run
        var newsDataQuery = NewsDataClient.BuildQuery(allKeywords);
        if (newsDataQuery.Length > 0)
        {
            rawItems.AddRange(await newsData.SearchAsync(newsDataQuery, "en", ct));
        }

        // 4. Deduplicate (URL hash + normalized title) within this run and against the DB
        var candidates = new List<(RawNewsItem Item, string Hash, string? TitleKey)>();
        var runHashes = new HashSet<string>(StringComparer.Ordinal);
        var runTitles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rawItems)
        {
            var hash = Deduplicator.ComputeUrlHash(item.Url);
            var titleKey = Deduplicator.ComputeTitleKey(item.Title);
            if (!runHashes.Add(hash) || (titleKey is not null && !runTitles.Add(titleKey)))
            {
                continue;
            }

            candidates.Add((item, hash, titleKey));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var hashes = candidates.Select(c => c.Hash).ToList();
        var existingHashes = (await db.NewsArticles
                .AsNoTracking()
                .Where(x => hashes.Contains(x.UrlHash))
                .Select(x => x.UrlHash)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var titleWindowStart = DateTime.UtcNow - TitleDedupWindow;
        var existingTitleKeys = (await db.NewsArticles
                .AsNoTracking()
                .Where(x => x.FetchedAtUtc >= titleWindowStart)
                .Select(x => x.Title)
                .ToListAsync(ct))
            .Select(Deduplicator.ComputeTitleKey)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var (item, hash, titleKey) in candidates)
        {
            if (existingHashes.Contains(hash) || (titleKey is not null && existingTitleKeys.Contains(titleKey)))
            {
                continue;
            }

            db.NewsArticles.Add(new NewsArticle
            {
                UrlHash = hash,
                Url = item.Url,
                Title = item.Title.Length > 500 ? item.Title[..500] : item.Title,
                Snippet = item.Snippet,
                SourceName = item.SourceName.Length > 150 ? item.SourceName[..150] : item.SourceName,
                Language = item.Language,
                PublishedAtUtc = item.PublishedAtUtc
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Ingested {Raw} raw items, saved {Added} new unique articles", rawItems.Count, added);
    }

    private async Task EnrichPendingArticlesAsync(CancellationToken ct)
    {
        await GiveUpOnStalePendingArticlesAsync(ct);

        var batchSize = options.Value.GeminiBatchSize;
        var pending = await db.NewsArticles
            .Where(x => !x.IsAiProcessed)
            .OrderByDescending(x => x.PublishedAtUtc)
            .Take(batchSize * 3)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        if (!gemini.IsConfigured)
        {
            // No AI configured: articles pass through unfiltered and without a summary (by design).
            foreach (var art in pending)
            {
                art.IsAiProcessed = true;
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        var teams = await db.Teams.AsNoTracking().Where(t => t.IsTracked).ToListAsync(ct);
        var players = await db.Players.AsNoTracking().Where(p => p.IsTracked).ToListAsync(ct);
        var trackedNames = teams.Select(t => t.Name).Concat(players.Select(p => p.FullName)).Distinct().ToList();

        foreach (var chunk in pending.Chunk(batchSize))
        {
            var batch = await gemini.EnrichBatchAsync(chunk, trackedNames, ct);
            if (!batch.Succeeded)
            {
                // Leave these (and the remaining chunks) unprocessed so a later run retries them;
                // a failure here is usually a rate limit, so further calls now would fail too.
                logger.LogWarning("Gemini enrichment failed; {Remaining} articles stay pending for the next run",
                    pending.Count(a => !a.IsAiProcessed));
                break;
            }

            var resultMap = batch.Articles
                .GroupBy(r => r.ArticleId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var art in chunk)
            {
                // An article Gemini skipped stays pending and is retried (or given up on after PendingAiMaxAge).
                if (!resultMap.TryGetValue(art.Id, out var r))
                {
                    continue;
                }

                art.IsRelevant = r.IsRelevant;
                art.Category = NormalizeCategory(r.Category);
                art.SummaryVi = string.IsNullOrWhiteSpace(r.SummaryVi) ? null : r.SummaryVi.Trim();
                art.IsAiProcessed = true;
                LinkMatchedEntities(art, r.MatchedEntities ?? [], teams, players);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task GiveUpOnStalePendingArticlesAsync(CancellationToken ct)
    {
        var threshold = DateTime.UtcNow - PendingAiMaxAge;
        var stale = await db.NewsArticles
            .Where(x => !x.IsAiProcessed && x.FetchedAtUtc < threshold)
            .ToListAsync(ct);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var art in stale)
        {
            // Unfiltered and out of date: keep it queryable but never push it to Telegram.
            art.IsAiProcessed = true;
            art.IsNotified = true;
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Gave up AI processing for {Count} articles pending longer than {MaxAge}", stale.Count, PendingAiMaxAge);
    }

    internal static string NormalizeCategory(string? category)
        => GeminiEnricherClient.Categories.FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? "Other";

    private void LinkMatchedEntities(
        NewsArticle article,
        IReadOnlyList<string> matchedNames,
        IReadOnlyList<Team> teams,
        IReadOnlyList<Player> players)
    {
        // Several returned names can resolve to the same entity ("Arsenal", "Arsenal FC");
        // ArticleEntities has a composite key, so each (type, id) may only be added once.
        var linked = new HashSet<(string Type, int Id)>();

        foreach (var name in matchedNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
        {
            var team = FindBestMatch(teams, t => t.Name, name);
            if (team is not null && linked.Add(("Team", team.Id)))
            {
                db.ArticleEntities.Add(new ArticleEntity { ArticleId = article.Id, EntityType = "Team", EntityId = team.Id });
            }

            var player = FindBestMatch(players, p => p.FullName, name);
            if (player is not null && linked.Add(("Player", player.Id)))
            {
                db.ArticleEntities.Add(new ArticleEntity { ArticleId = article.Id, EntityType = "Player", EntityId = player.Id });
            }
        }
    }

    /// <summary>Exact (case-insensitive) match first, then containment either way.</summary>
    private static T? FindBestMatch<T>(IReadOnlyList<T> items, Func<T, string> nameOf, string name) where T : class
        => items.FirstOrDefault(x => string.Equals(nameOf(x), name, StringComparison.OrdinalIgnoreCase))
           ?? items.FirstOrDefault(x =>
           {
               var candidate = nameOf(x);
               return candidate.Length > 0 &&
                      (candidate.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                       name.Contains(candidate, StringComparison.OrdinalIgnoreCase));
           });

    private async Task NotifyAsync(string modeLabel, CancellationToken ct)
    {
        if (!telegram.IsEnabled)
        {
            return;
        }

        var toNotify = await db.NewsArticles
            .Where(x => x.IsRelevant && x.IsAiProcessed && !x.IsNotified)
            .OrderByDescending(x => x.PublishedAtUtc)
            .Take(DigestMaxArticles)
            .ToListAsync(ct);

        if (toNotify.Count == 0)
        {
            return;
        }

        var delivered = await telegram.SendDigestAsync(toNotify, modeLabel, ct);
        foreach (var art in toNotify.Take(delivered))
        {
            art.IsNotified = true;
        }

        if (delivered > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Telegram digest: {Delivered}/{Candidates} articles delivered", delivered, toNotify.Count);
    }
}
