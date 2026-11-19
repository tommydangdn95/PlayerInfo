namespace PlayerInfo.Api.Data.Entities;

public sealed class Team
{
    public int Id { get; set; }

    /// <summary>ESPN team id (e.g. "359" = Arsenal). Null for teams not yet linked to ESPN.</summary>
    public string? EspnId { get; set; }

    /// <summary>ESPN league slug used for this team's roster/news calls (e.g. "eng.1").</summary>
    public string? LeagueSlug { get; set; }

    /// <summary>Optional API-Football team id, only used to sync transfers.</summary>
    public int? ApiFootballId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Country { get; set; }
    public string? LogoUrl { get; set; }
    public bool IsTracked { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Player> Players { get; set; } = [];
}

public sealed class Player
{
    public int Id { get; set; }

    /// <summary>ESPN athlete id.</summary>
    public string EspnId { get; set; } = string.Empty;

    public int? TeamId { get; set; }
    public Team? Team { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Position { get; set; }
    public int? Number { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsTracked { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WatchlistAlias
{
    public int Id { get; set; }
    public string EntityType { get; set; } = "Team"; // "Team" or "Player"
    public int EntityId { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public string Language { get; set; } = "en"; // "en" or "vi"
}
