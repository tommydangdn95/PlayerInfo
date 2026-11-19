using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PlayerInfo.Api.Logging;

/// <summary>
/// Records every outbound HTTP call of a typed client (method, redacted URL, status, duration,
/// request/response bodies truncated) into the in-memory live log, tagged with the running job.
/// Registered inside the resilience handler, so each retry attempt is a separate entry.
/// Headers are never recorded (API keys travel in headers); secrets in URLs are masked.
/// </summary>
public sealed partial class HttpTraceHandler(LiveLogBuffer buffer, string provider) : DelegatingHandler
{
    public const int MaxRequestBodyChars = 8 * 1024;
    public const int MaxResponseBodyChars = 16 * 1024;

    private static readonly HashSet<string> SecretQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "key", "token", "access_token", "secret", "password"
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = RedactUrl(request.RequestUri);
        var (requestBody, requestTruncated) = await ReadBodyAsync(request.Content, MaxRequestBodyChars, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Record("Warning", $"{request.Method} {url} → lỗi: {ex.Message}", ex, new HttpTrace(
                request.Method.Method, url, null, stopwatch.ElapsedMilliseconds,
                request.Content?.Headers.ContentType?.ToString(), requestBody, requestTruncated,
                null, null, null, false, $"{ex.GetType().Name}: {ex.Message}"));
            throw;
        }

        // Buffer the response so it can be read here and again by the client.
        string? responseBody = null;
        var responseTruncated = false;
        long? responseBytes = null;
        if (response.Content is not null)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            responseBytes = response.Content.Headers.ContentLength;
            (responseBody, responseTruncated) = await ReadBodyAsync(response.Content, MaxResponseBodyChars, cancellationToken);
        }

        var elapsed = stopwatch.ElapsedMilliseconds;
        var status = (int)response.StatusCode;
        Record(
            status >= 400 ? "Warning" : "Info",
            $"{request.Method} {url} → {status} ({elapsed} ms{(responseBytes is { } b ? ", " + FormatSize(b) : "")})",
            null,
            new HttpTrace(
                request.Method.Method, url, status, elapsed,
                request.Content?.Headers.ContentType?.ToString(), requestBody, requestTruncated,
                response.Content?.Headers.ContentType?.ToString(), responseBytes, responseBody, responseTruncated, null));

        return response;
    }

    /// <summary>Masks secret query values and the Telegram bot token in the path.</summary>
    public static string RedactUrl(Uri? uri)
    {
        if (uri is null)
        {
            return "";
        }

        var text = uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.OriginalString.Split('?')[0];
        text = TelegramBotToken().Replace(text, "/bot***/");

        var query = uri.IsAbsoluteUri ? uri.Query : (uri.OriginalString.Contains('?') ? "?" + uri.OriginalString.Split('?', 2)[1] : "");
        if (query.Length <= 1)
        {
            return text;
        }

        var parts = query[1..].Split('&').Select(pair =>
        {
            var eq = pair.IndexOf('=');
            var name = eq >= 0 ? pair[..eq] : pair;
            return eq >= 0 && SecretQueryKeys.Contains(Uri.UnescapeDataString(name)) ? $"{name}=***" : pair;
        });

        return $"{text}?{string.Join('&', parts)}";
    }

    private void Record(string level, string message, Exception? exception, HttpTrace trace)
    {
        var job = JobLogContext.Current;
        buffer.Add(level, provider, message, exception is null ? null : $"{exception.GetType().Name}: {exception.Message}", job?.Id, job?.Mode, trace);
    }

    private static async Task<(string? Body, bool Truncated)> ReadBodyAsync(HttpContent? content, int maxChars, CancellationToken ct)
    {
        if (content is null)
        {
            return (null, false);
        }

        var mediaType = content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !IsText(mediaType))
        {
            return ($"[{mediaType}, không hiển thị]", false);
        }

        try
        {
            var text = await content.ReadAsStringAsync(ct);
            return text.Length > maxChars ? (text[..maxChars], true) : (text, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or HttpRequestException)
        {
            return ($"[không đọc được body: {ex.Message}]", false);
        }
    }

    private static bool IsText(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
           || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
           || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
           || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes)
        => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    [GeneratedRegex(@"/bot[^/]+/")]
    private static partial Regex TelegramBotToken();
}

public static class HttpTraceExtensions
{
    /// <summary>Records this client's calls in the live log. Call after the resilience handler so each attempt is traced.</summary>
    public static IHttpClientBuilder AddLiveTrace(this IHttpClientBuilder builder, string provider)
        => builder.AddHttpMessageHandler(sp => new HttpTraceHandler(sp.GetRequiredService<LiveLogBuffer>(), provider));
}
