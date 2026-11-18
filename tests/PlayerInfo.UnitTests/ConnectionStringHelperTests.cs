using PlayerInfo.Api.Services;

namespace PlayerInfo.UnitTests;

public sealed class ConnectionStringHelperTests
{
    [Fact]
    public void Normalize_ConvertsNeonPostgresUriToNpgsqlFormat()
    {
        const string uri = "postgresql://alex:p%40ssword@ep-cool-rain-12345.ap-southeast-1.aws.neon.tech/neondb?sslmode=require";

        var result = ConnectionStringHelper.Normalize(uri);

        Assert.Contains("Host=ep-cool-rain-12345.ap-southeast-1.aws.neon.tech", result);
        Assert.Contains("Database=neondb", result);
        Assert.Contains("Username=alex", result);
        Assert.Contains("Password=p@ssword", result);
        Assert.Contains("SSL Mode=Require", result);
    }

    [Fact]
    public void Normalize_ReturnsFallback_WhenEmpty()
    {
        var result = ConnectionStringHelper.Normalize("   ");
        Assert.Contains("Host=localhost", result);
    }
}
