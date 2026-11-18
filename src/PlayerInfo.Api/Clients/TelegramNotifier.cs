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
    public async Task SendDigestAsync(
        IReadOnlyList<NewsArticle> articles,
        string mode,
        CancellationToken ct = default)
    {
        var cfg = options.Value;
        if (!cfg.Enabled || articles.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<b>⚽ Bản tin bóng đá ({mode}) - {DateTime.UtcNow.AddHours(7):HH:mm dd/MM}</b>");
        sb.AppendLine();

        foreach (var a in articles.Take(10))
        {
            var summary = string.IsNullOrWhiteSpace(a.SummaryVi) ? a.Title : a.SummaryVi;
            sb.AppendLine($"• <b>[{EscapeHtml(a.Category)}]</b> {EscapeHtml(summary)}");
            sb.AppendLine($"  <a href=\"{a.Url}\">{EscapeHtml(a.SourceName)}</a>");
        }

        if (articles.Count > 10)
        {
            sb.AppendLine($"\n<i>...và {articles.Count - 10} tin khác trong database.</i>");
        }

        var url = $"https://api.telegram.org/bot{cfg.BotToken}/sendMessage";
        using var res = await http.PostAsJsonAsync(url, new
        {
            chat_id = cfg.ChatId,
            text = sb.ToString(),
            parse_mode = "HTML",
            disable_web_page_preview = true
        }, ct);

        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("Telegram API returned {StatusCode}", res.StatusCode);
        }
    }

    private static string EscapeHtml(string input)
        => input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
