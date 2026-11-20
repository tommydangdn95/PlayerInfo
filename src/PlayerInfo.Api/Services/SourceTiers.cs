using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Services;

/// <summary>Source credibility tiers: defaults, matching, and re-applying them to stored articles.</summary>
public static class SourceTiers
{
    public const int Junk = 0;
    public const int Unknown = 3;

    /// <summary>Starting list, editable in the admin UI ("Nguồn tin"). Matched against source name or URL host.</summary>
    public static readonly IReadOnlyList<(string Pattern, int Tier)> Defaults =
    [
        // Tier 1: wire agencies, major broadcasters, official bodies
        ("ESPN", 1), ("BBC", 1), ("Sky Sports", 1), ("The Athletic", 1), ("Reuters", 1),
        ("Associated Press", 1), ("apnews.com", 1), ("PA Media", 1), ("The Guardian", 1),
        ("premierleague.com", 1), ("UEFA", 1),

        // Tier 2: established outlets and specialist/local papers
        ("Goal", 2), ("The Telegraph", 2), ("The Times", 2), ("The Independent", 2), ("Evening Standard", 2),
        ("Football London", 2), ("football.london", 2), ("Manchester Evening News", 2), ("Liverpool Echo", 2), ("CBS Sports", 2),
        ("Marca", 2), ("as.com", 2), ("Mundo Deportivo", 2), ("Sport.es", 2), ("Gazzetta", 2), ("Corriere dello Sport", 2),
        ("Kicker", 2), ("Bild", 2), ("L'Équipe", 2), ("lequipe", 2), ("RMC Sport", 2), ("Football Italia", 2),
        ("VnExpress", 2), ("Dân trí", 2), ("Tuổi Trẻ", 2), ("Thanh Niên", 2), ("Thể thao & Văn hóa", 2), ("Znews", 2),

        // Tier 3: tabloids and aggregators (fast, often wrong)
        ("The Sun", 3), ("Mirror", 3), ("Daily Express", 3), ("Daily Star", 3), ("Daily Mail", 3),
        ("TEAMtalk", 3), ("CaughtOffside", 3), ("Football Insider", 3), ("90min", 3), ("GiveMeSport", 3),
        ("Sportskeeda", 3), ("Football365", 3), ("Squawka", 3), ("HITC", 3), ("Yardbarker", 3), ("Fichajes", 3),
        ("24h", 3), ("Bongda", 3), ("Thể thao 247", 3), ("Soha", 3), ("Kenh14", 3),

        // Junk: betting and tipping sites, hidden from the app and never sent to Gemini
        ("FanDuel", 0), ("DraftKings", 0), ("BetMGM", 0), ("bet365", 0), ("Sportsbook", 0), ("Betting", 0),
        ("Oddschecker", 0), ("Odds", 0), ("Covers", 0), ("Casino", 0), ("Tipster", 0)
    ];

    /// <summary>Seeds the default rules into an empty table and applies them to recent articles.</summary>
    public static async Task EnsureDefaultsAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.NewsSources.AnyAsync(ct))
        {
            return;
        }

        db.NewsSources.AddRange(Defaults.Select(d => new NewsSource { Pattern = d.Pattern, Tier = d.Tier }));
        await db.SaveChangesAsync(ct);
        await ReapplyAsync(db, TimeSpan.FromDays(30), ct);
    }

    public static async Task<Matcher> LoadMatcherAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rules = await db.NewsSources.AsNoTracking().Select(s => new { s.Pattern, s.Tier }).ToListAsync(ct);
        return new Matcher(rules.Select(r => (r.Pattern, r.Tier)));
    }

    /// <summary>
    /// Applies the current rules to recent articles: updates their tier, hides junk, and gives articles that are
    /// no longer junk back to Gemini. Returns how many articles changed.
    /// </summary>
    public static async Task<int> ReapplyAsync(AppDbContext db, TimeSpan window, CancellationToken ct = default)
    {
        var matcher = await LoadMatcherAsync(db, ct);
        var since = DateTime.UtcNow - window;
        var articles = await db.NewsArticles.Where(a => a.FetchedAtUtc >= since).ToListAsync(ct);

        var changed = 0;
        foreach (var article in articles)
        {
            var tier = matcher.Match(article.SourceName, article.Url);
            if (tier == article.SourceTier)
            {
                continue;
            }

            if (tier == Junk)
            {
                HideAsJunk(article);
            }
            else if (article.SourceTier == Junk)
            {
                // No longer junk: let Gemini judge it (it is still never pushed to Telegram).
                article.IsRelevant = true;
                article.IsAiProcessed = false;
            }

            article.SourceTier = tier;
            changed++;
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    public static void HideAsJunk(NewsArticle article)
    {
        article.SourceTier = Junk;
        article.IsRelevant = false;
        article.IsHot = false;
        article.IsAiProcessed = true;
        article.IsNotified = true;
    }

    public sealed class Matcher
    {
        private readonly List<(string Pattern, int Tier)> _rules;

        public Matcher(IEnumerable<(string Pattern, int Tier)> rules)
        {
            // Longest pattern first, so "Daily Mail" beats "Mail" and "Sky Sports" beats "Sky".
            _rules = rules
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .Select(r => (r.Pattern.Trim(), r.Tier))
                .OrderByDescending(r => r.Item1.Length)
                .ToList();
        }

        public int Match(string? sourceName, string? url) => MatchRule(sourceName, url) ?? Unknown;

        /// <summary>The tier of the first (longest) matching rule, or null when no rule matches.</summary>
        public int? MatchRule(string? sourceName, string? url)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
            foreach (var (pattern, tier) in _rules)
            {
                if ((sourceName?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    host.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return tier;
                }
            }

            return null;
        }
    }
}
