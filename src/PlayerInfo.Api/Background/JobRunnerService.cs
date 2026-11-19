using System.Threading.Channels;
using PlayerInfo.Api.Logging;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Background;

/// <param name="JobId">Short id shown in the live log; generated when the job is queued.</param>
public sealed record JobRequest(CollectorMode Mode, DateTime RequestedAtUtc, string? JobId = null)
{
    public static string NewJobId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// Bounded channel queue + BackgroundService so cron triggers return 202 Accepted immediately.
/// </summary>
public sealed class JobQueue
{
    private readonly Channel<JobRequest> _channel = Channel.CreateBounded<JobRequest>(new BoundedChannelOptions(10)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public ValueTask EnqueueAsync(JobRequest request, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<JobRequest> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

public sealed class JobRunnerService(
    JobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<JobRunnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
        {
            // Every log line written while this job runs is tagged with its id in the live log.
            using var jobScope = JobLogContext.Begin(job.JobId ?? JobRequest.NewJobId(), job.Mode.ToString());
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<CollectorPipeline>();
                await pipeline.RunAsync(job.Mode, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unhandled error executing job mode={Mode}", job.Mode);
            }
        }
    }
}
