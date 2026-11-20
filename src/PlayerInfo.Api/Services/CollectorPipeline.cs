using System.Globalization;
using System.Text.RegularExpressions;
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
    EspnClient espn,
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

    /// <summary>Max stories in one Telegram digest, and the least important story that is pushed.</summary>
    public const int DigestMaxStories = 10;
    public const int DigestMinImportance = 3;

    /// <summary>Stories updated within this window are offered to Gemini so new articles can join them.</summary>
    public static readonly TimeSpan OpenStoryWindow = TimeSpan.FromHours(48);
    public const int MaxOpenStories = 60;

    /// <summary>Articles still not AI-processed after this long are given up on (not retried, not notified).</summary>
    public static readonly TimeSpan PendingAiMaxAge = TimeSpan.FromHours(48);
    public static readonly TimeSpan UntaggedBackfillWindow = TimeSpan.FromDays(30);

    /// <summary>How far back stored headlines are compared for title-based deduplication.</summary>
    public static readonly TimeSpan TitleDedupWindow = TimeSpan.FromDays(3);

    /// <summary>An undated ESPN injury with the same reason within this window is treated as the same injury.</summary>
    public static readonly TimeSpan OngoingInjuryWindow = TimeSpan.FromDays(30);

    /// <summary>ESPN articles requested per tracked team and run.</summary>
    public const int EspnNewsPerTeam = 10;

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

    /// <summary>Tracked teams linked to ESPN (team id + league): the unit for roster, injury and news calls.</summary>
    private Task<List<Team>> GetEspnTrackedTeamsAsync(CancellationToken ct)
        => db.Teams
            .AsNoTracking()
            .Where(t => t.IsTracked && t.EspnId != null && t.LeagueSlug != null)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

    /// <summary>Injuries of tracked teams' players, from the ESPN roster (no key, no quota).</summary>
    private async Task SyncInjuriesAsync(CancellationToken ct)
    {
        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

        foreach (var team in await GetEspnTrackedTeamsAsync(ct))
        {
            var injured = (await espn.GetRosterAsync(team.LeagueSlug!, team.EspnId!, ct))
                .Where(a => a.Injuries.Count > 0)
                .ToList();
            if (injured.Count == 0)
            {
                continue;
            }

            var playerIds = injured.Select(a => a.Id).ToList();
            var existing = await db.PlayerInjuries
                .AsNoTracking()
                .Where(x => playerIds.Contains(x.PlayerEspnId))
                .Select(x => new { x.PlayerEspnId, x.Reason, x.ReportedDateUtc })
                .ToListAsync(ct);
            var seenByDate = existing.Select(x => (x.PlayerEspnId, x.ReportedDateUtc)).ToHashSet();
            var recentReasons = existing
                .Where(x => x.ReportedDateUtc >= today - OngoingInjuryWindow)
                .Select(x => (x.PlayerEspnId, x.Reason.ToLowerInvariant()))
                .ToHashSet();

            var added = 0;
            foreach (var athlete in injured)
            {
                foreach (var injury in athlete.Injuries)
                {
                    // Without a date from ESPN, an injury still listed tomorrow is the same injury, not a new one.
                    if (injury.DateUtc is null && recentReasons.Contains((athlete.Id, injury.Reason.ToLowerInvariant())))
                    {
                        continue;
                    }

                    var reported = DateTime.SpecifyKind((injury.DateUtc ?? today).Date, DateTimeKind.Utc);
                    if (!seenByDate.Add((athlete.Id, reported)))
                    {
                        continue;
                    }

                    recentReasons.Add((athlete.Id, injury.Reason.ToLowerInvariant()));
                    db.PlayerInjuries.Add(new PlayerInjury
                    {
                        PlayerEspnId = athlete.Id,
                        PlayerName = athlete.Name,
                        TeamEspnId = team.EspnId!,
                        TeamName = team.Name,
                        Reason = injury.Reason,
                        Type = injury.Status,
                        ReportedDateUtc = reported
                    });
                    added++;
                }
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("{Team}: {Added} new injuries", team.Name, added);
        }
    }

    /// <summary>Results and upcoming matches of the configured leagues, from the ESPN scoreboard.</summary>
    private async Task SyncFixturesAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // ESPN dates are US dates: yesterday..tomorrow covers last night's results and today's games in UTC / VN time.
        var dates = new[] { today.AddDays(-1), today, today.AddDays(1) };

        foreach (var league in options.Value.Leagues)
        {
            var matches = new List<EspnMatch>();
            foreach (var date in dates)
            {
                matches.AddRange(await espn.GetScoreboardAsync(league.Slug, date, ct));
            }

            var distinct = matches.DistinctBy(m => m.EventId).ToList();
            if (distinct.Count == 0)
            {
                continue;
            }

            var ids = distinct.Select(m => m.EventId).ToList();
            var existing = await db.Fixtures
                .Where(x => ids.Contains(x.EspnEventId))
                .ToDictionaryAsync(x => x.EspnEventId, ct);

            foreach (var m in distinct)
            {
                if (!existing.TryGetValue(m.EventId, out var row))
                {
                    row = new Fixture { EspnEventId = m.EventId };
                    db.Fixtures.Add(row);
                }

                row.LeagueSlug = league.Slug;
                row.LeagueName = league.Name;
                row.HomeTeamEspnId = m.HomeTeamId;
                row.HomeTeamName = m.HomeTeamName;
                row.AwayTeamEspnId = m.AwayTeamId;
                row.AwayTeamName = m.AwayTeamName;
                row.HomeGoals = m.HomeScore;
                row.AwayGoals = m.AwayScore;
                row.StatusShort = m.StatusName;
                row.KickoffAtUtc = m.KickoffUtc;
                row.UpdatedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("{League}: {Count} fixtures synced ({New} new)", league.Slug, distinct.Count, distinct.Count - existing.Count);
        }
    }

    /// <summary>
    /// Recent transfers from API-Football (quota-guarded), only for tracked teams that have an
    /// API-Football id. ESPN has no transfer feed.
    /// </summary>
    private async Task SyncTransfersAsync(CancellationToken ct)
    {
        var trackedTeams = await db.Teams
            .AsNoTracking()
            .Where(t => t.IsTracked && t.ApiFootballId != null && t.ApiFootballId > 0)
            .ToListAsync(ct);

        var cutoff = DateTime.UtcNow.AddDays(-7);
        // Shared across teams: a transfer between two tracked teams shows up in both responses.
        var seen = new HashSet<(int PlayerId, DateTime Date)>();

        foreach (var team in trackedTeams)
        {
            var envelopes = await apiFootball.GetTransfersByTeamAsync(team.ApiFootballId!.Value, ct);

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

    /// <summary>Squads of tracked teams, from the ESPN roster.</summary>
    private async Task SyncRostersAsync(CancellationToken ct)
    {
        foreach (var team in await GetEspnTrackedTeamsAsync(ct))
        {
            var athletes = (await espn.GetRosterAsync(team.LeagueSlug!, team.EspnId!, ct))
                .DistinctBy(a => a.Id)
                .ToList();
            if (athletes.Count == 0)
            {
                continue;
            }

            var ids = athletes.Select(a => a.Id).ToList();
            var existing = await db.Players
                .Where(x => ids.Contains(x.EspnId))
                .ToDictionaryAsync(x => x.EspnId, ct);

            foreach (var a in athletes)
            {
                if (!existing.TryGetValue(a.Id, out var player))
                {
                    player = new Player { EspnId = a.Id, IsTracked = true };
                    db.Players.Add(player);
                }

                player.TeamId = team.Id;
                player.FullName = a.Name;
                player.Number = a.Jersey;
                player.Position = a.Position;
                player.PhotoUrl = a.PhotoUrl;
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

        // The team/player each item was fetched for, so articles are grouped by team even without Gemini.
        var sourceTags = new Dictionary<RawNewsItem, EntityRef>();
        void AddFor(IEnumerable<RawNewsItem> items, EntityRef entity)
        {
            foreach (var item in items)
            {
                rawItems.Add(item);
                sourceTags.TryAdd(item, entity);
            }
        }

        // 1. Google News RSS grouped by entity & language (with jitter to avoid IP throttling)
        var groupedQueries = aliases
            .GroupBy(a => new { a.EntityType, a.EntityId, a.Language })
            .Select(g => new
            {
                Entity = new EntityRef(g.Key.EntityType, g.Key.EntityId),
                g.Key.Language,
                Query = string.Join(" OR ", g.Select(x => $"\"{x.Keyword}\""))
            })
            .ToList();

        foreach (var q in groupedQueries)
        {
            AddFor(await rssNews.FetchGoogleNewsAsync(q.Query, q.Language, ct), q.Entity);
            await Task.Delay(Random.Shared.Next(1200, 2500), ct);
        }

        // 2. ESPN articles tagged with each tracked team (no key, no quota, highly relevant)
        foreach (var team in await GetEspnTrackedTeamsAsync(ct))
        {
            AddFor(await espn.GetTeamNewsAsync(team.LeagueSlug!, team.EspnId!, EspnNewsPerTeam, ct), new EntityRef("Team", team.Id));
        }

        // 3. Direct Sports RSS feeds filtered locally by tracked keywords
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

        // 4. NewsData.io (optional if key configured): one combined query per run
        var newsDataQuery = NewsDataClient.BuildQuery(allKeywords);
        if (newsDataQuery.Length > 0)
        {
            rawItems.AddRange(await newsData.SearchAsync(newsDataQuery, "en", ct));
        }

        // 5. Deduplicate (URL hash + normalized title) within this run and against the DB.
        //    A duplicate still contributes its tags: the same story fetched for two teams is about both.
        var matcher = new KeywordMatcher(aliases);
        var candidates = new List<(RawNewsItem Item, string Hash, string? TitleKey, HashSet<EntityRef> Tags)>();
        var byHash = new Dictionary<string, HashSet<EntityRef>>(StringComparer.Ordinal);
        var byTitle = new Dictionary<string, HashSet<EntityRef>>(StringComparer.Ordinal);
        foreach (var item in rawItems)
        {
            var tags = matcher.Match(item.Title, item.Snippet);
            if (sourceTags.TryGetValue(item, out var source))
            {
                tags.Add(source);
            }

            var hash = Deduplicator.ComputeUrlHash(item.Url);
            var titleKey = Deduplicator.ComputeTitleKey(item.Title);
            if (byHash.TryGetValue(hash, out var kept) || (titleKey is not null && byTitle.TryGetValue(titleKey, out kept)))
            {
                kept.UnionWith(tags);
                continue;
            }

            byHash[hash] = tags;
            if (titleKey is not null)
            {
                byTitle[titleKey] = tags;
            }

            candidates.Add((item, hash, titleKey, tags));
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

        var tiers = await SourceTiers.LoadMatcherAsync(db, ct);
        var added = 0;
        var junk = 0;
        foreach (var (item, hash, titleKey, tags) in candidates)
        {
            if (existingHashes.Contains(hash) || (titleKey is not null && existingTitleKeys.Contains(titleKey)))
            {
                continue;
            }

            var article = new NewsArticle
            {
                UrlHash = hash,
                Url = item.Url,
                Title = item.Title.Length > 500 ? item.Title[..500] : item.Title,
                Snippet = item.Snippet,
                SourceName = item.SourceName.Length > 150 ? item.SourceName[..150] : item.SourceName,
                Language = item.Language,
                PublishedAtUtc = item.PublishedAtUtc,
                SourceTier = tiers.Match(item.SourceName, item.Url),
                Entities = tags.Select(t => new ArticleEntity { EntityType = t.Type, EntityId = t.Id }).ToList()
            };

            if (article.SourceTier == SourceTiers.Junk)
            {
                // Betting/tipping sites: kept only so they are not fetched again; hidden and never sent to Gemini.
                SourceTiers.HideAsJunk(article);
                junk++;
            }

            db.NewsArticles.Add(article);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Ingested {Raw} raw items, saved {Added} new unique articles ({Junk} from junk sources, hidden)",
            rawItems.Count, added, junk);

        await TagUntaggedRecentArticlesAsync(matcher, ct);
    }

    /// <summary>
    /// Articles saved before ingest-time tagging existed (or before a keyword was added) get their teams
    /// from keyword matches, so the admin UI can group them by team.
    /// </summary>
    private async Task TagUntaggedRecentArticlesAsync(KeywordMatcher matcher, CancellationToken ct)
    {
        if (matcher.IsEmpty)
        {
            return;
        }

        var since = DateTime.UtcNow - UntaggedBackfillWindow;
        var untagged = await db.NewsArticles
            .AsNoTracking()
            .Where(x => x.PublishedAtUtc >= since && !x.Entities.Any())
            .Select(x => new { x.Id, x.Title, x.Snippet })
            .ToListAsync(ct);

        var tagged = 0;
        foreach (var art in untagged)
        {
            var tags = matcher.Match(art.Title, art.Snippet);
            foreach (var tag in tags)
            {
                db.ArticleEntities.Add(new ArticleEntity { ArticleId = art.Id, EntityType = tag.Type, EntityId = tag.Id });
            }

            tagged += tags.Count > 0 ? 1 : 0;
        }

        if (tagged > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Tagged {Count} older articles with their teams by keyword", tagged);
        }
    }

    internal readonly record struct EntityRef(string Type, int Id);

    /// <summary>Whole-word, case-insensitive match of watchlist keywords in a title or snippet.</summary>
    internal sealed class KeywordMatcher
    {
        private readonly List<(Regex Pattern, EntityRef Entity)> _patterns;

        public KeywordMatcher(IEnumerable<WatchlistAlias> aliases)
        {
            _patterns = aliases
                .Where(a => !string.IsNullOrWhiteSpace(a.Keyword))
                .Select(a => (
                    new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(a.Keyword.Trim())}(?![\p{{L}}\p{{N}}])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    new EntityRef(a.EntityType, a.EntityId)))
                .ToList();
        }

        public bool IsEmpty => _patterns.Count == 0;

        public HashSet<EntityRef> Match(string title, string? snippet)
        {
            var text = snippet is null ? title : $"{title}\n{snippet}";
            return _patterns.Where(p => p.Pattern.IsMatch(text)).Select(p => p.Entity).ToHashSet();
        }
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
            var chunkIds = chunk.Select(a => a.Id).ToList();
            var existingLinks = (await db.ArticleEntities
                    .Where(e => chunkIds.Contains(e.ArticleId))
                    .Select(e => new { e.ArticleId, e.EntityType, e.EntityId })
                    .ToListAsync(ct))
                .ToLookup(e => e.ArticleId, e => (e.EntityType, e.EntityId));

            // Re-read for every chunk: the previous chunk may have opened new stories.
            var openStories = await LoadOpenStoriesAsync(teams, ct);

            var batch = await gemini.EnrichBatchAsync(chunk, trackedNames, openStories, ct);
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
            var storyResults = batch.Stories
                .Where(st => !string.IsNullOrWhiteSpace(st.Ref))
                .GroupBy(st => st.Ref.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var openIds = openStories.Select(o => o.Id).ToHashSet();
            var storiesByRef = new Dictionary<string, Story>(StringComparer.OrdinalIgnoreCase);

            foreach (var art in chunk)
            {
                // An article Gemini skipped stays pending and is retried (or given up on after PendingAiMaxAge).
                if (!resultMap.TryGetValue(art.Id, out var r))
                {
                    continue;
                }

                art.IsRelevant = r.IsRelevant && art.SourceTier != SourceTiers.Junk;
                art.Category = NewsTaxonomy.NormalizeTopic(r.Topic);
                art.ClaimType = NewsTaxonomy.NormalizeClaimType(r.ClaimType);
                art.Importance = NewsTaxonomy.NormalizeImportance(r.Importance);
                art.IsHot = art.IsRelevant && art.Importance >= NewsTaxonomy.HotImportance;
                art.SummaryVi = string.IsNullOrWhiteSpace(r.SummaryVi) ? null : r.SummaryVi.Trim();
                art.IsAiProcessed = true;
                LinkMatchedEntities(art, r.MatchedEntities ?? [], teams, players, existingLinks[art.Id]);

                if (art.IsRelevant)
                {
                    art.Story = await ResolveStoryAsync(art, r.StoryRef, openIds, storiesByRef, ct);
                }
            }

            foreach (var (storyRef, story) in storiesByRef)
            {
                if (storyResults.TryGetValue(storyRef, out var st))
                {
                    if (!string.IsNullOrWhiteSpace(st.HeadlineVi))
                    {
                        story.HeadlineVi = Truncate(st.HeadlineVi.Trim(), 300);
                    }

                    story.SummaryVi = string.IsNullOrWhiteSpace(st.SummaryVi) ? story.SummaryVi : st.SummaryVi.Trim();
                    story.Topic = NewsTaxonomy.NormalizeTopic(st.Topic);
                    story.Importance = NewsTaxonomy.NormalizeImportance(st.Importance);
                }

                story.LastUpdatedUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            await RescoreStoriesAsync(storiesByRef.Values.Select(st => st.Id).ToList(), ct);
        }
    }

    /// <summary>Recent stories, with the tracked teams their articles are about, for Gemini to attach new articles to.</summary>
    private async Task<List<OpenStory>> LoadOpenStoriesAsync(IReadOnlyList<Team> teams, CancellationToken ct)
    {
        var since = DateTime.UtcNow - OpenStoryWindow;
        var stories = await db.Stories.AsNoTracking()
            .Where(st => st.LastUpdatedUtc >= since)
            .OrderByDescending(st => st.LastUpdatedUtc)
            .Take(MaxOpenStories)
            .Select(st => new { st.Id, st.Topic, st.HeadlineVi, st.SummaryVi })
            .ToListAsync(ct);
        if (stories.Count == 0)
        {
            return [];
        }

        var ids = stories.Select(st => st.Id).ToList();
        var teamLinks = await db.NewsArticles.AsNoTracking()
            .Where(a => a.StoryId != null && ids.Contains(a.StoryId.Value))
            .SelectMany(a => a.Entities.Where(e => e.EntityType == "Team"), (a, e) => new { StoryId = a.StoryId!.Value, e.EntityId })
            .Distinct()
            .ToListAsync(ct);
        var teamNames = teams.ToDictionary(t => t.Id, t => t.Name);
        var teamsByStory = teamLinks
            .Where(l => teamNames.ContainsKey(l.EntityId))
            .ToLookup(l => l.StoryId, l => teamNames[l.EntityId]);

        return stories
            .Select(st => new OpenStory(st.Id, string.Join(", ", teamsByStory[st.Id].Distinct()), st.Topic, st.HeadlineVi, st.SummaryVi))
            .ToList();
    }

    /// <summary>
    /// The story Gemini put the article in: an open story ("S12"), a story new in this batch ("N1", shared by
    /// the batch's articles about the same news), or, when Gemini gave nothing usable, a story of its own.
    /// </summary>
    private async Task<Story> ResolveStoryAsync(
        NewsArticle article,
        string? storyRef,
        HashSet<long> openIds,
        Dictionary<string, Story> storiesByRef,
        CancellationToken ct)
    {
        var key = storyRef?.Trim() ?? string.Empty;
        if (key.Length > 1 && (key[0] is 'S' or 's') && long.TryParse(key[1..], out var id) && openIds.Contains(id))
        {
            key = $"S{id}";
            if (!storiesByRef.TryGetValue(key, out var open))
            {
                open = (await db.Stories.FindAsync([id], ct))!;
                storiesByRef[key] = open;
            }

            return open;
        }

        if (key.Length == 0 || key[0] is 'S' or 's')
        {
            key = $"A{article.Id}"; // unknown or missing reference: its own story
        }

        if (!storiesByRef.TryGetValue(key, out var story))
        {
            story = new Story
            {
                HeadlineVi = Truncate(article.SummaryVi ?? article.Title, 300),
                SummaryVi = article.SummaryVi,
                Topic = article.Category,
                Importance = Math.Max(1, article.Importance),
                FirstSeenUtc = DateTime.UtcNow
            };
            db.Stories.Add(story);
            storiesByRef[key] = story;
        }

        return story;
    }

    /// <summary>Recomputes confidence, source count and best tier of stories from all their articles.</summary>
    private async Task RescoreStoriesAsync(List<long> storyIds, CancellationToken ct)
    {
        if (storyIds.Count == 0)
        {
            return;
        }

        var stories = await db.Stories.Where(st => storyIds.Contains(st.Id)).ToListAsync(ct);
        var articles = (await db.NewsArticles.Where(a => a.StoryId != null && storyIds.Contains(a.StoryId.Value)).ToListAsync(ct))
            .ToLookup(a => a.StoryId!.Value);
        foreach (var story in stories)
        {
            NewsTaxonomy.Rescore(story, articles[story.Id].ToList());
        }

        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

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

    private void LinkMatchedEntities(
        NewsArticle article,
        IReadOnlyList<string> matchedNames,
        IReadOnlyList<Team> teams,
        IReadOnlyList<Player> players,
        IEnumerable<(string Type, int Id)> alreadyLinked)
    {
        // Several returned names can resolve to the same entity ("Arsenal", "Arsenal FC"), and ingestion
        // may already have linked it; ArticleEntities has a composite key, so each (type, id) is added once.
        var linked = alreadyLinked.ToHashSet();

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

        if (gemini.IsConfigured)
        {
            await NotifyStoriesAsync(modeLabel, ct);
            return;
        }

        // Without AI there are no stories: push the newest articles as before.
        var toNotify = await db.NewsArticles
            .Where(x => x.IsRelevant && x.IsAiProcessed && !x.IsNotified)
            .OrderByDescending(x => x.IsHot)
            .ThenByDescending(x => x.PublishedAtUtc)
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

    /// <summary>
    /// Pushes stories that are new or were updated since they were last pushed (e.g. a rumour that became official),
    /// most important first, each with its best source.
    /// </summary>
    private async Task NotifyStoriesAsync(string modeLabel, CancellationToken ct)
    {
        var since = DateTime.UtcNow - TimeSpan.FromHours(24);
        var stories = await db.Stories
            .Where(st => st.LastUpdatedUtc >= since && st.Importance >= DigestMinImportance &&
                         (st.NotifiedAtUtc == null || st.NotifiedAtUtc < st.LastUpdatedUtc))
            .OrderByDescending(st => st.Importance)
            .ThenByDescending(st => st.SourceCount)
            .ThenByDescending(st => st.LastUpdatedUtc)
            .Take(DigestMaxStories)
            .ToListAsync(ct);

        if (stories.Count == 0)
        {
            return;
        }

        var ids = stories.Select(st => st.Id).ToList();
        var articles = (await db.NewsArticles
                .Where(a => a.StoryId != null && ids.Contains(a.StoryId.Value) && a.IsRelevant)
                .Include(a => a.Entities)
                .ToListAsync(ct))
            .ToLookup(a => a.StoryId!.Value);
        var teamNames = await db.Teams.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var items = stories.Select(st =>
        {
            var storyArticles = articles[st.Id].ToList();
            var best = storyArticles
                .OrderBy(a => a.SourceTier == SourceTiers.Junk ? 9 : a.SourceTier)
                .ThenByDescending(a => a.PublishedAtUtc)
                .FirstOrDefault();
            var teams = storyArticles
                .SelectMany(a => a.Entities)
                .Where(e => e.EntityType == "Team" && teamNames.ContainsKey(e.EntityId))
                .Select(e => teamNames[e.EntityId])
                .Distinct()
                .Order()
                .ToList();
            return new StoryDigestItem(st, teams, best?.Url, best?.SourceName);
        }).ToList();

        var delivered = await telegram.SendStoryDigestAsync(items, modeLabel, ct);
        var now = DateTime.UtcNow;
        foreach (var item in items.Take(delivered))
        {
            item.Story.NotifiedAtUtc = now;
            foreach (var art in articles[item.Story.Id])
            {
                art.IsNotified = true;
            }
        }

        if (delivered > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Telegram digest: {Delivered}/{Candidates} stories delivered", delivered, items.Count);
    }
}
