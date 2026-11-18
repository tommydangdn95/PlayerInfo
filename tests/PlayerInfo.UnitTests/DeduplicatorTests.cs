using PlayerInfo.Api.Services;

namespace PlayerInfo.UnitTests;

public sealed class DeduplicatorTests
{
    [Fact]
    public void ComputeUrlHash_StripsTrackingParamsFragmentTrailingSlashAndHostCase()
    {
        var url1 = "https://www.bbc.com/sport/football/articles/123?utm_source=google&fbclid=abc";
        var url2 = "HTTPS://WWW.BBC.COM/sport/football/articles/123/#comments";

        var hash1 = Deduplicator.ComputeUrlHash(url1);
        var hash2 = Deduplicator.ComputeUrlHash(url2);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public void ComputeUrlHash_PreservesNonTrackingQueryParams()
    {
        var url1 = "https://example.com/news?id=10";
        var url2 = "https://example.com/news?id=20";

        Assert.NotEqual(
            Deduplicator.ComputeUrlHash(url1),
            Deduplicator.ComputeUrlHash(url2));
    }

    [Fact]
    public void ComputeUrlHash_IsCaseSensitiveForPath()
    {
        // Google News article ids are case-sensitive base64.
        Assert.NotEqual(
            Deduplicator.ComputeUrlHash("https://news.google.com/rss/articles/CBMiAbC"),
            Deduplicator.ComputeUrlHash("https://news.google.com/rss/articles/CBMiabc"));
    }

    [Fact]
    public void ComputeTitleKey_IgnoresCasePunctuationAndVietnameseDiacritics()
    {
        Assert.Equal(
            Deduplicator.ComputeTitleKey("Arsenal win the North London derby!"),
            Deduplicator.ComputeTitleKey("  ARSENAL win the north-London derby "));

        Assert.Equal(
            Deduplicator.ComputeTitleKey("Đội tuyển Việt Nam thắng đậm"),
            Deduplicator.ComputeTitleKey("doi tuyen viet nam thang dam"));
    }

    [Fact]
    public void ComputeTitleKey_ReturnsNull_ForShortTitles()
    {
        Assert.Null(Deduplicator.ComputeTitleKey("Live"));
        Assert.Null(Deduplicator.ComputeTitleKey(null));
    }
}
