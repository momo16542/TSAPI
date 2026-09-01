using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace ExtDataJob;

/// <summary>
/// Telegram 通知。行為與 BotRateJob 的 TelegramNotify 相同（同一個 bot、同一個群組討論串），
/// 但不共用該檔——那支綁著 BotRateJob.Config，跨專案連結會把整組匯率設定一起拖進來。
///
/// 兩個刻意的設計：
///   · 沒設 KEY_VAULT_URL 就只寫 log 不送——本機跑不必為了通知去配金鑰庫。
///   · **通知失敗絕不讓 job 失敗**：抓取與寫入都成功了，只是訊息沒送出去，
///     回非 0 會讓排程誤判成資料有問題。
/// </summary>
internal sealed class Notifier
{
    private readonly ILogger _logger;

    public Notifier(ILogger logger) => _logger = logger;

    public async Task SendAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(Config.KeyVaultUrl))
        {
            _logger.LogInformation("未設定 KEY_VAULT_URL，略過 Telegram 通知：{Message}", message);
            return;
        }

        try
        {
            var secrets = new SecretClient(new Uri(Config.KeyVaultUrl), new Azure.Identity.DefaultAzureCredential());
            KeyVaultSecret secret = await secrets.GetSecretAsync("TelegramBot");

            using var http = new HttpClient();
            var form = new List<KeyValuePair<string, string>>
            {
                new("chat_id", Config.TelegramChatId),
                new("text", message),
            };
            if (!string.IsNullOrWhiteSpace(Config.TelegramThreadId))
            {
                form.Add(new KeyValuePair<string, string>("message_thread_id", Config.TelegramThreadId!));
            }

            var response = await http.PostAsync(
                $"https://api.telegram.org/bot{secret.Value}/sendMessage",
                new FormUrlEncodedContent(form));

            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) _logger.LogInformation("Telegram 傳送成功");
            else _logger.LogWarning("Telegram 傳送失敗 ({Status})：{Body}", (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram 傳送失敗（不影響 job 結果）");
        }
    }
}
