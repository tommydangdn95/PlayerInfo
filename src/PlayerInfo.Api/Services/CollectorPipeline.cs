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
    public async Task RunAsync(CollectorMode mode, CancellationToken ct = default)
    {
        logger.LogInformation("Starting CollectorPipeline in mode={Mode} at {TimeUtc}", mode, DateTime.UtcNow);

        switch (mode)
        {
            case CollectorMode.Roster:
                await SyncRostersAsync(ct);
                break;

            case CollectorMode.Full:
                await SyncInjuriesAndFixturesAsync(ct);
                await SyncTransfersAsync(ct);
                await IngestAndEnrichNewsAsync(mode.ToString(), ct);
                break;

            case CollectorMode.TransfersNews:
                await SyncTransfersAsync(ct);
                await IngestAndEnrichNewsAsync(mode.ToString(), ct);
                break;

            case CollectorMode.News:
                await IngestAndEnrichNewsAsync(mode.ToString(), ct);
                break;
        }

        logger.LogInformation("Finished CollectorPipeline in mode={Mode} at {TimeUtc}", mode, DateTime.UtcNow);
    }

    private async Task SyncInjuriesAndFixturesAsync(CancellationToken ct)
    {
        var cfg = options.Value;
        foreach (var leagueId in cfg.TrackedLeagueIds)
        {
            var injuries = await apiFootball.GetInjuriesByLeagueAsync(leagueId, cfg.CurrentSeason, ct);
            foreach (var item in injuries)
            {
                var reportedDate = DateTime.SpecifyKind(item.Fixture.Date.Date, DateTimeKind.Utc);
                var exists = await db.PlayerInjuries.AnyAsync(
                    x => x.PlayerExternalId == item.Player.Id && x.ReportedDateUtc == reportedDate, ct);

                if (!exists)
                {
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
                }
            }
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var date in new[] { today.AddDays(-1), today })
        {
            var fixtures = await apiFootball.GetFixturesByDateAsync(date, ct);
            var trackedLeagues = cfg.TrackedLeagueIds.ToHashSet();

            foreach (var f in fixtures.Where(x => trackedLeagues.Contains(x.League.Id)))
            {
                var existing = await db.Fixtures.FirstOrDefaultAsync(x => x.ExternalFixtureId == f.Fixture.Id, ct);
                if (existing is null)
                {
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
                        KickoffAtUtc = DateTime.SpecifyKind(f.Fixture.Date, DateTimeKind.Utc)
                    });
                }
                else
                {
                    existing.HomeGoals = f.Goals.Home;
                    existing.AwayGoals = f.Goals.Away;
                    existing.StatusShort = f.Fixture.Status.Short;
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SyncTransfersAsync(CancellationToken ct)
    {
        var trackedTeams = await db.Teams
            .AsNoTracking()
            .Where(t => t.IsTracked && t.ExternalApiId > 0)
            .ToListAsync(ct);

        var cutoff = DateTime.UtcNow.AddDays(-7);

        foreach (var team in trackedTeams)
        {
            var envelopes = await apiFootball.GetTransfersByTeamAsync(team.ExternalApiId, ct);
            foreach (var env in envelopes)
            {
                foreach (var detail in env.Transfers)
                {
                    if (!DateTime.TryParse(detail.Date, out var transferDate))
                    {
                        continue;
                    }

                    var transferUtc = DateTime.SpecifyKind(transferDate, DateTimeKind.Utc);
                    if (transferUtc < cutoff)
                    {
                        continue;
                    }

                    var exists = await db.PlayerTransfers.AnyAsync(
                        x => x.PlayerExternalId == env.Player.Id && x.TransferDateUtc == transferUtc, ct);

                    if (!exists)
                    {
                        db.PlayerTransfers.Add(new PlayerTransfer
                        {
                            PlayerExternalId = env.Player.Id,
                            PlayerName = env.Player.Name,
                            FromTeamExternalId = detail.Teams.Out.Id,
                            FromTeamName = detail.Teams.Out.Name,
                            ToTeamExternalId = detail.Teams.In.Id,
                            ToTeamName = detail.Teams.In.Name,
                            TransferType = detail.Type,
                            TransferDateUtc = transferUtc
                        });
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SyncRostersAsync(CancellationToken ct)
    {
        var trackedTeams = await db.Teams
            .Where(t => t.IsTracked && t.ExternalApiId > 0)
            .ToListAsync(ct);

        foreach (var team in trackedTeams)
        {
            var squads = await apiFootball.GetSquadByTeamAsync(team.ExternalApiId, ct);
            foreach (var squad in squads)
            {
                foreach (var p in squad.Players)
                {
                    var existing = await db.Players.FirstOrDefaultAsync(x => x.ExternalApiId == p.Id, ct);
                    if (existing is null)
                    {
                        db.Players.Add(new Player
                        {
                            ExternalApiId = p.Id,
                            TeamId = team.Id,
                            FullName = p.Name,
                            Number = p.Number,
                            Position = p.Position,
                            PhotoUrl = p.Photo,
                            IsTracked = true
                        });
                    }
                    else
                    {
                        existing.TeamId = team.Id;
                        existing.FullName = p.Name;
                        existing.Number = p.Number;
                        existing.Position = p.Position;
                        existing.PhotoUrl = p.Photo;
                        existing.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task IngestAndEnrichNewsAsync(string modeLabel, CancellationToken ct)
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
            var googleItems = await rssNews.FetchGoogleNewsAsync(q.Query, q.Language, ct);
            rawItems.AddRange(googleItems);
            await Task.Delay(Random.Shared.Next(1200, 2500), ct);
        }

        // 2. Direct Sports RSS feeds filtered locally by tracked keywords
        var allKeywords = aliases.Select(a => a.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var feed in options.Value.SportsRssFeeds)
        {
            var feedItems = await rssNews.FetchSportsFeedAsync(feed, ct);
            if (allKeywords.Count == 0)
            {
                rawItems.AddRange(feedItems);
            }
            else
            {
                rawItems.AddRange(feedItems.Where(item =>
                    allKeywords.Any(kw =>
                        item.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        (item.Snippet?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false))));
            }
        }

        // 3. NewsData.io (optional if key configured, using combined top keywords)
        if (allKeywords.Count > 0)
        {
            var newsDataQuery = string.Join(" OR ", allKeywords.Take(5));
            var ndItems = await newsData.SearchAsync(newsDataQuery, "en", ct);
            rawItems.AddRange(ndItems);
        }

        // 4. Deduplicate by URL Hash and save new articles
        var newlyAdded = new List<NewsArticle>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in rawItems)
        {
            var hash = Deduplicator.ComputeUrlHash(item.Url);
            if (!seenHashes.Add(hash))
            {
                continue;
            }

            var existsInDb = await db.NewsArticles.AnyAsync(x => x.UrlHash == hash, ct);
            if (existsInDb)
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
                PublishedAtUtc = item.PublishedAtUtc
            };

            db.NewsArticles.Add(article);
            newlyAdded.Add(article);
        }

        if (newlyAdded.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Saved {Count} new unique articles", newlyAdded.Count);
        }

        // 5. Enrich unprocessed articles in batches using Gemini AI
        await EnrichPendingArticlesAsync(ct);

        // 6. Send Telegram digest for relevant, unnotified articles
        var toNotify = await db.NewsArticles
            .Where(x => x.IsRelevant && !x.IsNotified)
            .OrderByDescending(x => x.PublishedAtUtc)
            .Take(20)
            .ToListAsync(ct);

        if (toNotify.Count > 0)
        {
            await telegram.SendDigestAsync(toNotify, modeLabel, ct);
            foreach (var art in toNotify)
            {
                art.IsNotified = true;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task EnrichPendingArticlesAsync(CancellationToken ct)
    {
        var pending = await db.NewsArticles
            .Where(x => !x.IsAiProcessed)
            .OrderByDescending(x => x.PublishedAtUtc)
            .Take(options.Value.GeminiBatchSize * 3)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        var teams = await db.Teams.AsNoTracking().Where(t => t.IsTracked).ToListAsync(ct);
        var players = await db.Players.AsNoTracking().Where(p => p.IsTracked).ToListAsync(ct);
        var trackedNames = teams.Select(t => t.Name).Concat(players.Select(p => p.FullName)).Distinct().ToList();

        foreach (var chunk in pending.Chunk(options.Value.GeminiBatchSize))
        {
            var results = await gemini.EnrichBatchAsync(chunk, trackedNames, ct);
            if (results.Count == 0)
            {
                // Gemini not configured or failed; mark processed so we don't block notifications
                foreach (var art in chunk)
                {
                    art.IsAiProcessed = true;
                }
                continue;
            }

            var resultMap = results.ToDictionary(r => r.ArticleId);
            foreach (var art in chunk)
            {
                if (resultMap.TryGetValue(art.Id, out var r))
                {
                    art.IsRelevant = r.IsRelevant;
                    art.Category = string.IsNullOrWhiteSpace(r.Category) ? "Other" : r.Category;
                    art.SummaryVi = r.SummaryVi;
                    LinkMatchedEntities(art, r.MatchedEntities, teams, players);
                }

                art.IsAiProcessed = true;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private void LinkMatchedEntities(
        NewsArticle article,
        IReadOnlyList<string> matchedNames,
        IReadOnlyList<Team> teams,
        IReadOnlyList<Player> players)
    {
        foreach (var name in matchedNames)
        {
            var team = teams.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                                 name.Contains(t.Name, StringComparison.OrdinalIgnoreCase));
            if (team is not null)
            {
                db.ArticleEntities.Add(new ArticleEntity
                {
                    ArticleId = article.Id,
                    EntityType = "Team",
                    EntityId = team.Id
                });
            }

            var player = players.FirstOrDefault(p => p.FullName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                                     name.Contains(p.FullName, StringComparison.OrdinalIgnoreCase));
            if (player is not null)
            {
                db.ArticleEntities.Add(new ArticleEntity
                {
                    ArticleId = article.Id,
                    EntityType = "Player",
                    EntityId = player.Id
                });
            }
        }
    }
}
