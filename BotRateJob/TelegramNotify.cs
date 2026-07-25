using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace BotRateJob;

/// <summary>
/// 與 Function 專案相同的通知方式：token 放 Key Vault，用受控識別存取。
/// 未設定 KEY_VAULT_URL 時（例如本機測試）只寫 log，不中斷流程。
/// </summary>
public sealed class TelegramNotify
{
    private readonly ILogger _logger;

    public TelegramNotify(ILogger logger) => _logger = logger;

    public async Task SendAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(Config.KeyVaultUrl))
        {
            _logger.LogInformation("未設定 KEY_VAULT_URL，略過 Telegram 通知：{Message}", message);
            return;
        }

        try
        {
            var secrets = new SecretClient(new Uri(Config.KeyVaultUrl), new DefaultAzureCredential());
            KeyVaultSecret secret = await secrets.GetSecretAsync("TelegramBot");

            using var http = new HttpClient();
            var form = new List<KeyValuePair<string, string>>
            {
                new("chat_id", Config.TelegramChatId),
                new("text", message)
            };
            if (!string.IsNullOrWhiteSpace(Config.TelegramThreadId))
            {
                form.Add(new KeyValuePair<string, string>("message_thread_id", Config.TelegramThreadId!));
            }

            var response = await http.PostAsync(
                $"https://api.telegram.org/bot{secret.Value}/sendMessage",
                new FormUrlEncodedContent(form));

            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Telegram 傳送成功");
            }
            else
            {
                _logger.LogWarning("Telegram 傳送失敗 ({Status}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // 通知失敗不應該讓整個 job 失敗
            _logger.LogError(ex, "Telegram 傳送失敗");
        }
    }
}
