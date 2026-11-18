using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HealthController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Lightweight wake-up and readiness probe for cron-job.org and Render.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return Ok(new
        {
            status = canConnect ? "healthy" : "degraded",
            utcTime = DateTime.UtcNow
        });
    }
}
