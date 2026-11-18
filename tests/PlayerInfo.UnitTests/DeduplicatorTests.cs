using PlayerInfo.Api.Services;

namespace PlayerInfo.UnitTests;

public sealed class DeduplicatorTests
{
    [Fact]
    public void ComputeUrlHash_StripsTrackingParamsAndNormalizesCase()
    {
        var url1 = "https://www.bbc.com/sport/football/articles/123?utm_source=google&fbclid=abc";
        var url2 = "HTTPS://WWW.BBC.COM/sport/football/articles/123/";

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
}
