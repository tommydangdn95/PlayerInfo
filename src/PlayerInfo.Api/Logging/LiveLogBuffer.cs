using System.Threading.Channels;

namespace PlayerInfo.Api.Logging;

public sealed record LiveLogEntry(
    long Id,
    DateTime TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? Exception,
    string? JobId,
    string? JobMode,
    HttpTrace? Http = null);

/// <summary>One outbound HTTP call (secrets redacted, bodies truncated). See <c>HttpTraceHandler</c>.</summary>
public sealed record HttpTrace(
    string Method,
    string Url,
    int? StatusCode,
    long DurationMs,
    string? RequestContentType,
    string? RequestBody,
    bool RequestTruncated,
    string? ResponseContentType,
    long? ResponseBytes,
    string? ResponseBody,
    bool ResponseTruncated,
    string? Error);

/// <summary>
/// In-memory ring buffer of recent application log entries, with live subscribers (the admin
/// "Nhật ký" page). Nothing is persisted: entries are lost on restart, by design.
/// </summary>
public sealed class LiveLogBuffer
{
    public const int Capacity = 1000;
    public const int MaxMessageLength = 2000;
    private const int SubscriberQueueLength = 500;

    private readonly object _lock = new();
    private readonly Queue<LiveLogEntry> _entries = new();
    private readonly List<Channel<LiveLogEntry>> _subscribers = [];
    private readonly TimeProvider _time;
    private long _lastId;

    public LiveLogBuffer(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Add(string level, string category, string message, string? exception, string? jobId, string? jobMode, HttpTrace? http = null)
    {
        lock (_lock)
        {
            var entry = new LiveLogEntry(
                ++_lastId,
                _time.GetUtcNow().UtcDateTime,
                level,
                category,
                Truncate(message),
                exception is null ? null : Truncate(exception),
                jobId,
                jobMode,
                http);

            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }

            foreach (var subscriber in _subscribers)
            {
                // Bounded + DropOldest: a slow viewer never blocks logging or grows memory.
                subscriber.Writer.TryWrite(entry);
            }
        }
    }

    /// <summary>Entries with an id greater than <paramref name="afterId"/>, oldest first (at most <paramref name="max"/>, the newest ones).</summary>
    public IReadOnlyList<LiveLogEntry> GetAfter(long afterId, int max = Capacity)
    {
        lock (_lock)
        {
            return TakeLast(_entries.Where(e => e.Id > afterId), max);
        }
    }

    /// <summary>
    /// Atomically returns the backlog after <paramref name="afterId"/> and starts a live feed of later
    /// entries, so nothing is missed or duplicated between the two. Dispose the subscription to stop.
    /// </summary>
    public LiveLogSubscription Subscribe(long afterId)
    {
        var channel = Channel.CreateBounded<LiveLogEntry>(new BoundedChannelOptions(SubscriberQueueLength)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        lock (_lock)
        {
            var backlog = TakeLast(_entries.Where(e => e.Id > afterId), Capacity);
            _subscribers.Add(channel);
            return new LiveLogSubscription(backlog, channel.Reader, () => Unsubscribe(channel));
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    private void Unsubscribe(Channel<LiveLogEntry> channel)
    {
        lock (_lock)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    private static List<LiveLogEntry> TakeLast(IEnumerable<LiveLogEntry> entries, int max)
    {
        var list = entries.ToList();
        return list.Count <= max ? list : list.GetRange(list.Count - max, max);
    }

    private static string Truncate(string value)
        => value.Length <= MaxMessageLength ? value : value[..MaxMessageLength] + "…";
}

public sealed class LiveLogSubscription(
    IReadOnlyList<LiveLogEntry> backlog,
    ChannelReader<LiveLogEntry> reader,
    Action unsubscribe) : IDisposable
{
    private int _disposed;

    public IReadOnlyList<LiveLogEntry> Backlog { get; } = backlog;
    public ChannelReader<LiveLogEntry> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unsubscribe();
        }
    }
}
