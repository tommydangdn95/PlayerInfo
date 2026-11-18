using System.Security.Cryptography;
using System.Text;

namespace PlayerInfo.Api.Services;

/// <summary>
/// Normalizes article URLs (stripping tracking query params) and computes SHA-256 hashes for deduplication.
/// </summary>
public static class Deduplicator
{
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "fbclid", "gclid", "oc", "ref"
    };

    public static string NormalizeUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return rawUrl.Trim().ToLowerInvariant();
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        if (!string.IsNullOrEmpty(uri.Query))
        {
            var kept = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair =>
                {
                    var eqIndex = pair.IndexOf('=');
                    var key = eqIndex > 0 ? pair[..eqIndex] : pair;
                    return !TrackingParams.Contains(Uri.UnescapeDataString(key));
                });

            builder.Query = string.Join('&', kept);
        }

        return builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }

    public static string ComputeUrlHash(string rawUrl)
    {
        var normalized = NormalizeUrl(rawUrl);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }
}
