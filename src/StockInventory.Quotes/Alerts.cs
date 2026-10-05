using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StockInventory.Quotes;

public interface IAlertSender
{
    /// <summary>寄信失敗只記日誌,不影響主流程(§7.6)。</summary>
    Task SendAsync(string subject, string body, CancellationToken ct = default);
}

public sealed class SmtpAlertSender(IOptions<SmtpOptions> smtp, IOptions<AlertOptions> alert, ILogger<SmtpAlertSender> log) : IAlertSender
{
    public async Task SendAsync(string subject, string body, CancellationToken ct = default)
    {
        var s = smtp.Value;
        var to = alert.Value.To;
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.From) || to.Length == 0)
        {
            log.LogWarning("未設定 Smtp/Alert:To,略過通知信:{Subject}", subject);
            return;
        }
        try
        {
            using var client = new SmtpClient(s.Host, s.Port) { EnableSsl = true };
            if (!string.IsNullOrEmpty(s.UserName)) client.Credentials = new NetworkCredential(s.UserName, s.Password);
            using var msg = new MailMessage { From = new MailAddress(s.From), Subject = subject, Body = body };
            foreach (var t in to) msg.To.Add(t);
            await client.SendMailAsync(msg, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("通知信寄送失敗 ({Type})", ex.GetType().Name); // 不記錄密碼與信件內容
        }
    }
}

/// <summary>§7.6 報價中斷:stale 時寄一封,之後每 CooldownMinutes 最多再寄一封,恢復時寄恢復通知。</summary>
public sealed class StaleAlertSubscriber(IAlertSender sender, IOptions<AlertOptions> options, TimeProvider clock) : IQuoteSubscriber
{
    private bool _alerting;
    private DateTimeOffset _lastSent;

    public async Task OnTickAsync(MarketStatusSnapshot s, bool statusChanged, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var stale = s.State == Core.MarketState.Open && s.Stale;
        if (stale)
        {
            if (!_alerting || now - _lastSent >= TimeSpan.FromMinutes(options.Value.CooldownMinutes))
            {
                _alerting = true;
                _lastSent = now;
                await sender.SendAsync("[股票庫存] 報價中斷",
                    $"報價超過門檻時間未成功更新。最後成功更新(UTC):{s.LastFetchedAtUtc:O}", ct);
            }
        }
        else if (_alerting)
        {
            _alerting = false;
            await sender.SendAsync("[股票庫存] 報價已恢復", $"報價已恢復更新。最後成功更新(UTC):{s.LastFetchedAtUtc:O}", ct);
        }
    }

    public Task OnQuotesAppliedAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>§7.6 登入異常:同一帳號或來源 IP 在視窗內失敗達門檻時寄一封,套用冷卻時間。</summary>
public sealed class LoginFailureAlerts(IAlertSender sender, IOptions<AlertOptions> options, TimeProvider clock)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _fails = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new();

    public async Task RecordFailureAsync(string? userName, string? ip, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var hits = new List<(string Key, string Label, int Count)>();
        lock (_lock)
        {
            foreach (var (key, label) in new[] { ("u:" + (userName ?? "").Trim().ToLowerInvariant(), "帳號"), ("ip:" + (ip ?? ""), "來源 IP") })
            {
                if (key is "u:" or "ip:") continue;
                if (!_fails.TryGetValue(key, out var q)) _fails[key] = q = new Queue<DateTimeOffset>();
                q.Enqueue(now);
                var window = TimeSpan.FromMinutes(options.Value.LoginFailWindowMinutes);
                while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
                if (q.Count >= options.Value.LoginFailThreshold
                    && (!_lastSent.TryGetValue(key, out var last) || now - last >= TimeSpan.FromMinutes(options.Value.CooldownMinutes)))
                {
                    _lastSent[key] = now;
                    hits.Add((key, label, q.Count));
                }
            }
            Prune(now);
        }
        foreach (var h in hits)
            await sender.SendAsync("[股票庫存] 登入異常",
                $"同一{h.Label}在 {options.Value.LoginFailWindowMinutes} 分鐘內登入失敗 {h.Count} 次:{h.Key[(h.Key.IndexOf(':') + 1)..]}", ct);
    }

    private void Prune(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(options.Value.LoginFailWindowMinutes);
        foreach (var k in _fails.Where(kv => kv.Value.Count == 0 || now - kv.Value.Peek() > window).Select(kv => kv.Key).ToList())
            _fails.Remove(k);
    }
}
