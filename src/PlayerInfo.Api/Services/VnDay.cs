using System.Globalization;

namespace PlayerInfo.Api.Services;

/// <summary>Calendar days in Vietnam time (UTC+7, no daylight saving), as used by the admin UI.</summary>
public static class VnDay
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    public static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow + Offset);

    /// <summary>Parses yyyy-MM-dd; null or invalid means today.</summary>
    public static DateOnly ParseOrToday(string? day)
        => DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Today();

    /// <summary>[from, to) in UTC covering the Vietnam day.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) UtcRange(DateOnly day)
    {
        var from = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue) - Offset, DateTimeKind.Utc);
        return (from, from.AddDays(1));
    }
}
