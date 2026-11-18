using System.Net;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;

namespace PlayerInfo.UnitTests.Helpers;

public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(handler(request));
}

public static class TestDbFactory
{
    public static AppDbContext CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage XmlResponse(string xml, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
        };
}
