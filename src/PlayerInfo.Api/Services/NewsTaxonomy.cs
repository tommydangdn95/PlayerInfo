using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Services;

/// <summary>Topics, claim types and story confidence shared by Gemini, the pipeline and the API.</summary>
public static class NewsTaxonomy
{
    public static readonly IReadOnlyList<string> Topics =
    [
        "Transfer",     // signings, sales, loans, contract extensions
        "Injury",       // injuries, returns, fitness, squad availability
        "Match",        // results, match reports, performances
        "PreMatch",     // previews, predicted line-ups, pre-match press conferences
        "Manager",      // coach and staff: appointments, sackings, statements
        "Turmoil",      // internal problems: rifts, unrest, crisis, poor form
        "Discipline",   // cards, bans, investigations, charges
        "Club",         // ownership, finances, stadium, business
        "Other"
    ];

    public static readonly IReadOnlyList<string> ClaimTypes = ["Official", "Report", "Rumour", "Opinion"];

    public const string Official = "Official";
    public const string Confirmed = "Confirmed";
    public const string Rumour = "Rumour";
    public const string Opinion = "Opinion";

    /// <summary>Importance from which a story/article counts as hot news.</summary>
    public const int HotImportance = 4;

    private static readonly Dictionary<string, string> LegacyTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MatchPerformance"] = "Match",
        ["OffPitch"] = "Other"
    };

    public static string TopicEmoji(string topic) => topic switch
    {
        "Transfer" => "🔁",
        "Injury" => "🏥",
        "Match" => "⚽",
        "PreMatch" => "📋",
        "Manager" => "👔",
        "Turmoil" => "⚠️",
        "Discipline" => "🟥",
        "Club" => "💰",
        _ => "📰"
    };

    public static string ConfidenceLabelVi(string confidence) => confidence switch
    {
        Official => "✅ Chính thức",
        Confirmed => "🟢 Nhiều nguồn uy tín",
        Opinion => "💬 Bình luận",
        _ => "🟠 Tin đồn"
    };

    public static string NormalizeTopic(string? topic)
    {
        var value = topic?.Trim();
        if (value is not null && LegacyTopics.TryGetValue(value, out var mapped))
        {
            return mapped;
        }

        return Topics.FirstOrDefault(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)) ?? "Other";
    }

    public static string? NormalizeClaimType(string? claim)
        => ClaimTypes.FirstOrDefault(c => string.Equals(c, claim?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static int NormalizeImportance(int importance) => Math.Clamp(importance, 1, 5);

    /// <summary>Lowercased source name, so "BBC Sport" from two feeds counts once.</summary>
    public static string SourceKey(string sourceName) => sourceName.Trim().ToLowerInvariant();

    /// <summary>
    /// How sure we are, from the articles of a story:
    /// Official = an official announcement carried by a reliable (tier 1-2) source;
    /// Confirmed = reported (not just rumoured) by a tier-1 source or by 2+ distinct tier 1-2 sources;
    /// Opinion = only opinion pieces; otherwise Rumour.
    /// </summary>
    public static string ComputeConfidence(IReadOnlyCollection<NewsArticle> articles)
    {
        var reliable = articles.Where(a => a.SourceTier is 1 or 2).ToList();
        if (reliable.Any(a => a.ClaimType == Official))
        {
            return Official;
        }

        var reporting = reliable.Where(a => a.ClaimType is Official or "Report").ToList();
        if (reporting.Any(a => a.SourceTier == 1) ||
            reporting.Select(a => SourceKey(a.SourceName)).Distinct().Count() >= 2)
        {
            return Confirmed;
        }

        if (articles.Count > 0 && articles.All(a => a.ClaimType == Opinion))
        {
            return Opinion;
        }

        return Rumour;
    }

    /// <summary>Recomputes a story's confidence, source count and best tier from its articles.</summary>
    public static void Rescore(Story story, IReadOnlyCollection<NewsArticle> articles)
    {
        var live = articles.Where(a => a.IsRelevant).ToList();
        story.Confidence = ComputeConfidence(live);
        story.SourceCount = live.Select(a => SourceKey(a.SourceName)).Distinct().Count();
        story.BestTier = live.Where(a => a.SourceTier > 0).Select(a => a.SourceTier).DefaultIfEmpty(3).Min();
    }
}
