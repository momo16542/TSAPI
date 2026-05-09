using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace TSAPI
{
    internal class TelegramNotify
    {
        private static string keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL")
                                            ?? throw new InvalidOperationException("KEY_VAULT_URL 環境變數未設定");
        private static string chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")
                                       ?? "1207160975"; // 預設可寫死測試用
        private static string threadId = Environment.GetEnvironmentVariable("TELEGRAM_THREAD_ID");

        public static async Task SendNotify(string message, ILogger log)
        {
            try
            {
                // 1. 從 Key Vault 讀取 Token
                var client = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
                KeyVaultSecret secret = await client.GetSecretAsync("TelegramBot");
                string token = secret.Value;

                // 2. 呼叫 Telegram API
                using var http = new HttpClient();
                var url = $"https://api.telegram.org/bot{token}/sendMessage";

                var form = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("chat_id", chatId),
                    new KeyValuePair<string, string>("text", message)
                };
                if (!string.IsNullOrWhiteSpace(threadId))
                    form.Add(new KeyValuePair<string, string>("message_thread_id", threadId));

                var response = await http.PostAsync(url, new FormUrlEncodedContent(form));
                string result = await response.Content.ReadAsStringAsync();

                log.LogInformation($"Telegram 傳送成功: {result}");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Telegram 傳送失敗");
            }
        }
    }
}
