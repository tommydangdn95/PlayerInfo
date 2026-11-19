using System.Globalization;
using System.Text.Json;

namespace PlayerInfo.Api.Clients;

public sealed record EspnTeam(string Id, string Name, string? ShortName, string? Abbreviation, string? LogoUrl);

public sealed record EspnMatch(
    string EventId,
    DateTime KickoffUtc,
    string StatusName,
    string State,
    string? HomeTeamId,
    string HomeTeamName,
    int? HomeScore,
    string? AwayTeamId,
    string AwayTeamName,
    int? AwayScore);

public sealed record EspnInjury(string Status, string Reason, DateTime? DateUtc);

public sealed record EspnAthlete(
    string Id,
    string Name,
    string? Position,
    int? Jersey,
    string? PhotoUrl,
    IReadOnlyList<EspnInjury> Injuries);

/// <summary>
/// ESPN public soccer API (site.api.espn.com): no key and no daily quota, but unofficial, so every
/// method logs and returns an empty result instead of throwing when ESPN fails or changes shape.
/// Do not send a User-Agent containing "bot": ESPN's CDN rejects it with 403.
/// </summary>
public sealed class EspnClient(HttpClient http, ILogger<EspnClient> logger)
{
    public const string BaseUrl = "https://site.api.espn.com/apis/site/v2/sports/soccer/";

    /// <summary>All teams of a league (e.g. "eng.1").</summary>
    public async Task<IReadOnlyList<EspnTeam>> GetLeagueTeamsAsync(string leagueSlug, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"{leagueSlug}/teams", ct);
        if (doc is null)
        {
            return [];
        }

        try
        {
            var teams = new List<EspnTeam>();
            foreach (var sport in Array(doc.RootElement, "sports"))
            foreach (var league in Array(sport, "leagues"))
            foreach (var entry in Array(league, "teams"))
            {
                if (!entry.TryGetProperty("team", out var t) || Str(t, "id") is not { } id || Str(t, "displayName") is not { } name)
                {
                    continue;
                }

                var logo = Array(t, "logos").Select(l => Str(l, "href")).FirstOrDefault(h => h is not null);
                teams.Add(new EspnTeam(id, name, Str(t, "shortDisplayName"), Str(t, "abbreviation"), logo));
            }

            return teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            logger.LogWarning(ex, "Unexpected ESPN teams payload for {League}", leagueSlug);
            return [];
        }
    }

    /// <summary>
    /// Matches of a league on an ESPN date. ESPN dates follow US time, so callers should request
    /// one extra day around the UTC range they care about.
    /// </summary>
    public async Task<IReadOnlyList<EspnMatch>> GetScoreboardAsync(string leagueSlug, DateOnly date, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"{leagueSlug}/scoreboard?dates={date:yyyyMMdd}", ct);
        if (doc is null)
        {
            return [];
        }

        var matches = new List<EspnMatch>();
        foreach (var e in Array(doc.RootElement, "events"))
        {
            try
            {
                if (ParseMatch(e) is { } match)
                {
                    matches.Add(match);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
            {
                logger.LogWarning(ex, "Skipping unexpected ESPN event in {League} scoreboard", leagueSlug);
            }
        }

        return matches;
    }

    /// <summary>Current squad of a team, with each player's injuries (if ESPN lists any).</summary>
    public async Task<IReadOnlyList<EspnAthlete>> GetRosterAsync(string leagueSlug, string teamId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"{leagueSlug}/teams/{Uri.EscapeDataString(teamId)}/roster", ct);
        if (doc is null)
        {
            return [];
        }

        var athletes = new List<EspnAthlete>();
        foreach (var a in Array(doc.RootElement, "athletes"))
        {
            if (Str(a, "id") is not { } id || (Str(a, "displayName") ?? Str(a, "fullName")) is not { } name)
            {
                continue;
            }

            var position = a.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Object
                ? Str(p, "displayName") ?? Str(p, "abbreviation")
                : null;
            var photo = a.TryGetProperty("headshot", out var h) && h.ValueKind == JsonValueKind.Object ? Str(h, "href") : null;
            var jersey = int.TryParse(Str(a, "jersey"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) ? j : (int?)null;

            athletes.Add(new EspnAthlete(id, name, position, jersey, photo, Array(a, "injuries").Select(ParseInjury).ToList()));
        }

        return athletes;
    }

    /// <summary>Latest ESPN articles tagged with a team, as raw news items.</summary>
    public async Task<IReadOnlyList<RawNewsItem>> GetTeamNewsAsync(string leagueSlug, string teamId, int limit, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"{leagueSlug}/news?team={Uri.EscapeDataString(teamId)}&limit={limit}", ct);
        if (doc is null)
        {
            return [];
        }

        var items = new List<RawNewsItem>();
        foreach (var a in Array(doc.RootElement, "articles"))
        {
            var url = a.TryGetProperty("links", out var links) && links.TryGetProperty("web", out var web) ? Str(web, "href") : null;
            var title = Str(a, "headline");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var snippet = Str(a, "description");
            if (snippet is { Length: > 400 })
            {
                snippet = snippet[..400];
            }

            var published = ParseUtc(Str(a, "published")) ?? DateTime.UtcNow;
            items.Add(new RawNewsItem(url, title.Trim(), snippet, "ESPN", "en", published));
        }

        return items;
    }

    internal static EspnMatch? ParseMatch(JsonElement e)
    {
        if (Str(e, "id") is not { } id || !e.TryGetProperty("competitions", out var comps) || comps.GetArrayLength() == 0)
        {
            return null;
        }

        var c = comps[0];
        var kickoff = ParseUtc(Str(c, "date") ?? Str(e, "date"));
        if (kickoff is null)
        {
            return null;
        }

        var type = c.TryGetProperty("status", out var status) && status.TryGetProperty("type", out var t) ? t : default;
        var statusName = type.ValueKind == JsonValueKind.Object ? Str(type, "name") ?? "" : "";
        var state = type.ValueKind == JsonValueKind.Object ? Str(type, "state") ?? "" : "";

        var competitors = Array(c, "competitors").ToList();
        var home = competitors.FirstOrDefault(x => Str(x, "homeAway") == "home");
        var away = competitors.FirstOrDefault(x => Str(x, "homeAway") == "away");
        if (home.ValueKind != JsonValueKind.Object || away.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // ESPN sends score "0" for matches that have not started.
        var hasScore = state is "in" or "post";
        return new EspnMatch(
            id,
            kickoff.Value,
            statusName,
            state,
            TeamId(home),
            TeamName(home),
            hasScore ? Score(home) : null,
            TeamId(away),
            TeamName(away),
            hasScore ? Score(away) : null);
    }

    internal static EspnInjury ParseInjury(JsonElement injury)
    {
        // ESPN's soccer injury entries are sparse and vary; read the common shapes defensively.
        var status = Str(injury, "status") ?? "Injured";
        string? reason = null;
        if (injury.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            var parts = new[] { Str(details, "type"), Str(details, "detail"), Str(details, "location") }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            reason = string.Join(" - ", parts);
        }

        if (string.IsNullOrWhiteSpace(reason) && injury.TryGetProperty("type", out var type))
        {
            reason = type.ValueKind == JsonValueKind.Object ? Str(type, "description") ?? Str(type, "name") : type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        }

        return new EspnInjury(status, string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason!, ParseUtc(Str(injury, "date")));
    }

    private async Task<JsonDocument?> GetAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(relativeUrl, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("ESPN returned {StatusCode} for {Url}", res.StatusCode, relativeUrl);
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ESPN request failed for {Url}", relativeUrl);
            return null;
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    private static string? Str(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static string? TeamId(JsonElement competitor)
        => competitor.TryGetProperty("team", out var t) ? Str(t, "id") : null;

    private static string TeamName(JsonElement competitor)
        => competitor.TryGetProperty("team", out var t) ? Str(t, "displayName") ?? Str(t, "name") ?? "?" : "?";

    private static int? Score(JsonElement competitor)
    {
        if (!competitor.TryGetProperty("score", out var s))
        {
            return null;
        }

        return s.ValueKind switch
        {
            JsonValueKind.Number => (int)s.GetDouble(),
            JsonValueKind.String when int.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
            JsonValueKind.Object when s.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number => (int)value.GetDouble(),
            _ => null
        };
    }

    private static DateTime? ParseUtc(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime
            : null;
}
