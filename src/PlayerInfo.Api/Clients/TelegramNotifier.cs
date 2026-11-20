using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Clients;

/// <summary>
/// Sends concise Vietnamese news digests to a Telegram chat if configured.
/// </summary>
public sealed class TelegramNotifier(
    HttpClient http,
    IOptions<TelegramOptions> options,
    ILogger<TelegramNotifier> logger)
{
    /// <summary>Telegram rejects messages over 4096 characters; keep a margin.</summary>
    public const int MaxMessageLength = 4000;

    public bool IsEnabled => options.Value.Enabled;

    /// <summary>
    /// Sends a digest built from the start of <paramref name="articles"/>.
    /// Returns how many articles (from the start of the list) were delivered: only those
    /// should be marked as notified. Returns 0 if sending failed.
    /// </summary>
    public async Task<int> SendDigestAsync(
        IReadOnlyList<NewsArticle> articles,
        string mode,
        CancellationToken ct = default)
    {
        var cfg = options.Value;
        if (!cfg.Enabled || articles.Count == 0)
        {
            return 0;
        }

        var (text, included) = BuildMessage(articles, mode, DateTime.UtcNow);
        if (included == 0)
        {
            return 0;
        }

        try
        {
            var url = $"https://api.telegram.org/bot{cfg.BotToken}/sendMessage";
            using var res = await http.PostAsJsonAsync(url, new
            {
                chat_id = cfg.ChatId,
                text,
                parse_mode = "HTML",
                disable_web_page_preview = true
            }, ct);

            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Telegram API returned {StatusCode}: {Body}", res.StatusCode, body);
                return 0;
            }

            return included;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Telegram digest failed to send");
            return 0;
        }
    }

    internal static (string Text, int Included) BuildMessage(IReadOnlyList<NewsArticle> articles, string mode, DateTime utcNow)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>⚽ Bản tin bóng đá ({WebUtility.HtmlEncode(mode)}) - {utcNow.AddHours(7):HH:mm dd/MM}</b>");
        sb.AppendLine();

        var included = 0;
        foreach (var a in articles)
        {
            var summary = string.IsNullOrWhiteSpace(a.SummaryVi) ? a.Title : a.SummaryVi;
            var entry =
                $"{(a.IsHot ? "🔥" : "•")} <b>[{WebUtility.HtmlEncode(a.Category)}]</b> {WebUtility.HtmlEncode(summary)}\n" +
                $"  <a href=\"{WebUtility.HtmlEncode(a.Url)}\">{WebUtility.HtmlEncode(a.SourceName)}</a>\n";

            if (sb.Length + entry.Length > MaxMessageLength)
            {
                break;
            }

            sb.Append(entry);
            included++;
        }

        return (sb.ToString(), included);
    }
}
