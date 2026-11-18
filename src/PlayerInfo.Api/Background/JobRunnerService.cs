using System.Threading.Channels;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Background;

public sealed record JobRequest(CollectorMode Mode, DateTime RequestedAtUtc);

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
