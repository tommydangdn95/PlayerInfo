using System.Text;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Clients;

/// <summary>A story to push, with the teams it is about and its best source.</summary>
public sealed record StoryDigestItem(Story Story, IReadOnlyList<string> Teams, string? Url, string? SourceName);

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
        return included > 0 && await SendAsync(text, ct) ? included : 0;
    }

    /// <summary>
    /// Sends a digest built from the start of <paramref name="stories"/>. Returns how many stories (from the
    /// start of the list) were delivered, or 0 if sending failed.
    /// </summary>
    public async Task<int> SendStoryDigestAsync(IReadOnlyList<StoryDigestItem> stories, string mode, CancellationToken ct = default)
    {
        if (!options.Value.Enabled || stories.Count == 0)
        {
            return 0;
        }

        var (text, included) = BuildStoryMessage(stories, mode, DateTime.UtcNow);
        return included > 0 && await SendAsync(text, ct) ? included : 0;
    }

    private async Task<bool> SendAsync(string text, CancellationToken ct)
    {
        var cfg = options.Value;
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
                return false;
            }

            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Telegram digest failed to send");
            return false;
        }
    }

    /// <summary>
    /// Escapes only what Telegram's HTML mode requires (&amp; &lt; &gt; &quot;), so Vietnamese text stays readable
    /// instead of turning into numeric entities.
    /// </summary>
    internal static string Html(string? value)
        => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    internal static (string Text, int Included) BuildMessage(IReadOnlyList<NewsArticle> articles, string mode, DateTime utcNow)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>⚽ Bản tin bóng đá ({Html(mode)}) - {utcNow.AddHours(7):HH:mm dd/MM}</b>");
        sb.AppendLine();

        var included = 0;
        foreach (var a in articles)
        {
            var summary = string.IsNullOrWhiteSpace(a.SummaryVi) ? a.Title : a.SummaryVi;
            var entry =
                $"{(a.IsHot ? "🔥" : "•")} <b>[{Html(a.Category)}]</b> {Html(summary)}\n" +
                $"  <a href=\"{Html(a.Url)}\">{Html(a.SourceName)}</a>\n";

            if (sb.Length + entry.Length > MaxMessageLength)
            {
                break;
            }

            sb.Append(entry);
            included++;
        }

        return (sb.ToString(), included);
    }

    internal static (string Text, int Included) BuildStoryMessage(IReadOnlyList<StoryDigestItem> stories, string mode, DateTime utcNow)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>⚽ Bản tin bóng đá ({Html(mode)}) - {utcNow.AddHours(7):HH:mm dd/MM}</b>");
        sb.AppendLine();

        var included = 0;
        foreach (var item in stories)
        {
            var st = item.Story;
            var meta = new List<string>();
            if (item.Teams.Count > 0)
            {
                meta.Add(string.Join(", ", item.Teams));
            }

            meta.Add(NewsTaxonomy.ConfidenceLabelVi(st.Confidence));
            meta.Add($"{st.SourceCount} nguồn");

            var entry = new StringBuilder();
            entry.Append($"{(st.Importance >= NewsTaxonomy.HotImportance ? "🔥" : NewsTaxonomy.TopicEmoji(st.Topic))} ");
            entry.AppendLine($"<b>{Html(st.HeadlineVi)}</b>");
            entry.AppendLine($"<i>{Html(string.Join(" · ", meta))}</i>");
            if (!string.IsNullOrWhiteSpace(st.SummaryVi))
            {
                entry.AppendLine(Html(st.SummaryVi));
            }

            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                entry.AppendLine($"<a href=\"{Html(item.Url)}\">{Html(item.SourceName ?? "Nguồn")}</a>");
            }

            entry.AppendLine();
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
