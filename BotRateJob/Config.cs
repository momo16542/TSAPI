namespace BotRateJob;

internal static class Config
{
    public static string CosmosConnectionString =>
        Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING")
        ?? throw new InvalidOperationException("COSMOS_CONNECTION_STRING 環境變數未設定");

    public static string CosmosDatabase =>
        Environment.GetEnvironmentVariable("COSMOS_DATABASE") ?? "TSAPI";

    public static string CosmosContainer =>
        Environment.GetEnvironmentVariable("COSMOS_CONTAINER") ?? "BankTaiwanSpotRate";

    public static string? KeyVaultUrl => Environment.GetEnvironmentVariable("KEY_VAULT_URL");

    public static string TelegramChatId =>
        Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "-5030274644";

    public static string? TelegramThreadId =>
        Environment.GetEnvironmentVariable("TELEGRAM_THREAD_ID") ?? "2";

    /// <summary>跳過 CSV 直接走表格解析。用於驗證備援路徑，或 CSV 端點哪天失效時的臨時開關。</summary>
    public static bool ForceTableParse
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("FORCE_TABLE_PARSE");
            return v is "1" or "true" or "True" or "TRUE";
        }
    }

    /// <summary>本機測試用：只抓資料並印出，不寫 Cosmos、不發 Telegram。</summary>
    public static bool DryRun
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DRY_RUN");
            return v is "1" or "true" or "True" or "TRUE";
        }
    }
}
