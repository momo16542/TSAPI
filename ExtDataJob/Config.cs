namespace ExtDataJob;

/// <summary>
/// 全部設定走環境變數（同 BotRateJob 的做法）：Container Apps Job 的設定介面就是環境變數，
/// 也才不會有連線字串被 commit 進 repo 的風險。
/// </summary>
internal static class Config
{
    /// <summary>中央庫伺服器，例：yyerp.database.windows.net</summary>
    public static string SqlServer =>
        Environment.GetEnvironmentVariable("CENTRAL_SQL_SERVER")
        ?? throw new InvalidOperationException("CENTRAL_SQL_SERVER 環境變數未設定");

    public static string SqlDatabase =>
        Environment.GetEnvironmentVariable("CENTRAL_SQL_DATABASE") ?? "YanyueCentral";

    /// <summary>
    /// 連線字串刻意不含任何帳密：Azure 上用受控識別取權杖，本機用 az CLI。
    /// 見 CentralDbWriter 的 accessTokenProvider 與 memory azure-sql-auth-from-dev-machine。
    /// </summary>
    public static string SqlConnectionString =>
        $"Server={SqlServer};Database={SqlDatabase};Encrypt=True;Connect Timeout=60";

    /// <summary>原始檔落地根目錄。容器內給掛載路徑；不設就用暫存資料夾。</summary>
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("EXTDATA_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "ExternalData");

    /// <summary>只解析這個月（含）之後的工作表；不設＝全部 288 張（首次灌檔用）。</summary>
    public static DateTime? SinceMonth
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("EXTDATA_SINCE_MONTH");
            return DateTime.TryParse(v, out var d) ? new DateTime(d.Year, d.Month, 1) : null;
        }
    }

    /// <summary>只跑指定來源（TTVMA_QTA1／TTVMA_TTA1／METALPRICE…），逗號分隔；不設＝全部。</summary>
    public static string[] SourceKeys =>
        (Environment.GetEnvironmentVariable("EXTDATA_SOURCES") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>不寫 DB，只抓取與解析後印出統計。上線前驗證用。</summary>
    public static bool DryRun =>
        (Environment.GetEnvironmentVariable("DRY_RUN") ?? "").Trim() is "1" or "true" or "True";

    /// <summary>強制重抓，即使本期已有副本。</summary>
    public static bool ForceRefetch =>
        (Environment.GetEnvironmentVariable("FORCE_REFETCH") ?? "").Trim() is "1" or "true" or "True";

    public static string? KeyVaultUrl => Environment.GetEnvironmentVariable("KEY_VAULT_URL");

    public static string TelegramChatId =>
        Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "-1003784964525";

    public static string? TelegramThreadId =>
        Environment.GetEnvironmentVariable("TELEGRAM_THREAD_ID") ?? "2";
}
