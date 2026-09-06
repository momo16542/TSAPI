namespace TgosRelay;

/// <summary>
/// 全部設定走環境變數（systemd <c>EnvironmentFile=/etc/tgos-relay.env</c>，權限 600）。
/// 不用 appsettings.json：金鑰不該有機會被 publish 進部署包或進 repo。
/// </summary>
public sealed class RelayConfig
{
    /// <summary>YYAPI 打過來要帶的共用密鑰（header <c>X-Relay-Key</c>）。空＝服務拒收（503，不是靜默放行）。</summary>
    public string RelayKey { get; init; } = "";

    /// <summary>TGOS 核准後拿到的成對金鑰。空＝<c>/geocode</c> 回 503 明確訊息；<c>/healthz</c> 照常 200。</summary>
    public string TgosAppId { get; init; } = "";
    public string TgosApiKey { get; init; } = "";

    public string TgosUrl { get; init; } = 預設TgosUrl;

    /// <summary>
    /// 實測後若發現 TGOS 的 X 是緯度、Y 是經度，設 <c>TGOS_XY_SWAP=true</c>。
    /// 預設 false＝X 經度、Y 緯度（EPSG:4326 的定義，核准前未實測）。
    /// </summary>
    public bool XySwap { get; init; }

    /// <summary>兩次打 TGOS 的最小間隔（毫秒）。TGOS 未公布配額，且使用規定第七條第 5 款禁短時間大量查詢，預設 1000。</summary>
    public int MinIntervalMs { get; init; } = 1000;

    public const string 預設TgosUrl = "https://addr.tgos.tw/addrws/v30/QueryAddr.asmx/QueryAddr";

    public bool RelayKey已設 => !string.IsNullOrWhiteSpace(RelayKey);
    public bool Tgos金鑰已設 => !string.IsNullOrWhiteSpace(TgosAppId) && !string.IsNullOrWhiteSpace(TgosApiKey);

    public static RelayConfig FromEnvironment()
    {
        static string E(string k) => (Environment.GetEnvironmentVariable(k) ?? "").Trim();
        var url = E("TGOS_URL");
        return new RelayConfig
        {
            RelayKey = E("RELAY_KEY"),
            TgosAppId = E("TGOS_APPID"),
            TgosApiKey = E("TGOS_APIKEY"),
            TgosUrl = url.Length == 0 ? 預設TgosUrl : url,
            XySwap = string.Equals(E("TGOS_XY_SWAP"), "true", StringComparison.OrdinalIgnoreCase),
            MinIntervalMs = int.TryParse(E("TGOS_MIN_INTERVAL_MS"), out var ms) && ms >= 0 ? ms : 1000,
        };
    }
}
