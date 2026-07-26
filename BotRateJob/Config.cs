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

    // 原本的 -5030274644 已失效：該群組升級為超級群組後 id 改變，
    // 送出會得到 400 group chat was upgraded to a supergroup chat
    public static string TelegramChatId =>
        Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "-1003784964525";

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

    /// <summary>回填模式起始日（yyyy-MM-dd）。設定後改為抓取區間而非最新牌價。</summary>
    public static DateTime? BackfillFrom => ParseDate("BACKFILL_FROM");

    /// <summary>回填模式結束日（含）。未設定時預設為今天。</summary>
    public static DateTime? BackfillTo => ParseDate("BACKFILL_TO");

    private static DateTime? ParseDate(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (DateTime.TryParse(v, out var parsed)) return parsed.Date;
        throw new InvalidOperationException($"{name} 格式無法解析：{v}（應為 yyyy-MM-dd）");
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
