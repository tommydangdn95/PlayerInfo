namespace PlayerInfo.Api.Logging;

/// <summary>Tags log entries written while a collector job runs (flows across awaits).</summary>
public static class JobLogContext
{
    private static readonly AsyncLocal<(string Id, string Mode)?> CurrentJob = new();

    public static (string Id, string Mode)? Current => CurrentJob.Value;

    public static IDisposable Begin(string jobId, string mode)
    {
        var previous = CurrentJob.Value;
        CurrentJob.Value = (jobId, mode);
        return new Scope(() => CurrentJob.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>
/// Copies the application's own logs (categories starting with "PlayerInfo", Information and above)
/// into <see cref="LiveLogBuffer"/>. Framework/HttpClient categories are skipped, so request URLs
/// (which can carry tokens) never reach the buffer.
/// </summary>
public sealed class LiveLogLoggerProvider(LiveLogBuffer buffer) : ILoggerProvider
{
    public const string CategoryPrefix = "PlayerInfo";

    public ILogger CreateLogger(string categoryName)
        => categoryName.StartsWith(CategoryPrefix, StringComparison.Ordinal)
            ? new LiveLogLogger(buffer, ShortCategory(categoryName))
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    /// <summary>"PlayerInfo.Api.Services.CollectorPipeline" → "CollectorPipeline".</summary>
    internal static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private sealed class LiveLogLogger(LiveLogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var job = JobLogContext.Current;
            buffer.Add(
                LevelName(logLevel),
                category,
                formatter(state, exception),
                exception is null ? null : $"{exception.GetType().Name}: {exception.Message}",
                job?.Id,
                job?.Mode);
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Information => "Info",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => level.ToString()
        };
    }
}
