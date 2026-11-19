namespace PlayerInfo.Api.Data.Entities;

/// <summary>Injury of a player of a tracked team, from the ESPN roster.</summary>
public sealed class PlayerInjury
{
    public long Id { get; set; }
    public string PlayerEspnId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string TeamEspnId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;

    /// <summary>What is injured / why the player is out (e.g. "Hamstring").</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Availability status (e.g. "Out", "Day-To-Day").</summary>
    public string? Type { get; set; }

    public DateTime ReportedDateUtc { get; set; }
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Transfer from API-Football (ids are API-Football player/team ids).</summary>
public sealed class PlayerTransfer
{
    public long Id { get; set; }
    public int PlayerExternalId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int? FromTeamExternalId { get; set; }
    public string FromTeamName { get; set; } = string.Empty;
    public int? ToTeamExternalId { get; set; }
    public string ToTeamName { get; set; } = string.Empty;
    public string? TransferType { get; set; }
    public DateTime TransferDateUtc { get; set; }
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Match of a tracked league, from the ESPN scoreboard.</summary>
public sealed class Fixture
{
    public long Id { get; set; }
    public string EspnEventId { get; set; } = string.Empty;
    public string LeagueSlug { get; set; } = string.Empty;
    public string LeagueName { get; set; } = string.Empty;
    public string? HomeTeamEspnId { get; set; }
    public string HomeTeamName { get; set; } = string.Empty;
    public string? AwayTeamEspnId { get; set; }
    public string AwayTeamName { get; set; } = string.Empty;
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }

    /// <summary>ESPN status (e.g. "STATUS_SCHEDULED", "STATUS_FULL_TIME").</summary>
    public string StatusShort { get; set; } = string.Empty;

    public DateTime KickoffAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ApiQuotaLog
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty; // "ApiFootball", "NewsData", "Gemini"
    public DateOnly LogDate { get; set; }
    public int RequestCount { get; set; }
}
