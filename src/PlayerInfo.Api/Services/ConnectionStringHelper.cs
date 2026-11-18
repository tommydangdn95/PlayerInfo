using Npgsql;

namespace PlayerInfo.Api.Services;

/// <summary>
/// Normalizes PostgreSQL connection strings from either ADO.NET key-value format or Neon's postgresql:// URI format.
/// </summary>
public static class ConnectionStringHelper
{
    public static string Normalize(string? raw, string fallback = "Host=localhost;Database=playerinfo;Username=postgres;Password=postgres")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var uri = new Uri(trimmed);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty,
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }
}
