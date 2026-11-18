using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlayerInfo.Api.Services;

/// <summary>
/// Normalizes article URLs (stripping tracking query params) and computes SHA-256 hashes for deduplication.
/// Also builds a normalized title key to catch the same story arriving under different URLs
/// (e.g. a Google News redirect link and the publisher's own RSS link).
/// </summary>
public static class Deduplicator
{
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "fbclid", "gclid", "oc", "ref"
    };

    /// <summary>
    /// Lowercases only scheme and host (paths and query values can be case-sensitive,
    /// e.g. Google News article ids), drops the fragment, tracking params and trailing slash.
    /// </summary>
    public static string NormalizeUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return rawUrl.Trim();
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Path = uri.AbsolutePath.TrimEnd('/')
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

        // Uri already lowercases scheme and host; default ports are dropped.
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static string ComputeUrlHash(string rawUrl)
    {
        var normalized = NormalizeUrl(rawUrl);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Case-, accent- and punctuation-insensitive key for a headline. Returns null for
    /// headlines too short to be a reliable duplicate signal.
    /// </summary>
    public static string? ComputeTitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var decomposed = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch == 'đ' || ch == 'Đ' ? 'd' : ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        var key = sb.ToString().Trim();
        return key.Length >= 20 ? key : null;
    }
}
