namespace TgosRelay;

/// <summary>
/// 全部設定走環境變數（systemd <c>EnvironmentFile=/etc/tgos-relay.env</c>，權限 600）。
/// 不用 appsettings.json：金鑰不該有機會被 publish 進部署包或進 repo。
/// </summary>
public sealed class RelayConfig
{
    /// <summary>YYAPI 打過來要帶的共用密鑰（header <c>X-Relay-Key</c>）。空＝服務拒收（503，不是靜默放行）。</summary>
    public string RelayKey { get; init; } = "";

    /// <summary>
    /// 「地址查詢坐標服務」（正查 QueryAddr）核准後拿到的成對金鑰。
    /// 空＝<c>/geocode</c> 回 503 明確訊息；<c>/healthz</c> 照常 200。
    /// </summary>
    public string TgosAppId { get; init; } = "";
    public string TgosApiKey { get; init; } = "";

    public string TgosUrl { get; init; } = 預設TgosUrl;

    /// <summary>
    /// 「坐標回傳門牌服務」（反查 GeoQueryAddr）核准後拿到的成對金鑰。
    /// ⚠️ 這是**另一支服務、要另外申請**：TGOS 的三支服務各自申請、各自一組 APPId/APIKey，
    /// **不可與 <see cref="TgosAppId"/>／<see cref="TgosApiKey"/> 共用**
    /// （拿錯金鑰打過去只會回一段純文字 <c>Length of the data to decrypt is invalid.</c>，
    /// 沒有明確錯誤碼，很容易被誤判成「服務壞了」）。
    /// 空＝<c>/reverse</c> 回 503 明確訊息；<c>/geocode</c> 與 <c>/healthz</c> 不受影響。
    /// </summary>
    public string TgosGeoAppId { get; init; } = "";
    public string TgosGeoApiKey { get; init; } = "";

    public string TgosGeoUrl { get; init; } = 預設TgosGeoUrl;

    /// <summary>
    /// 實測後若發現 TGOS 的 X 是緯度、Y 是經度，設 <c>TGOS_XY_SWAP=true</c>。
    /// 預設 false＝X 經度、Y 緯度（EPSG:4326 的定義，核准前未實測）。
    /// </summary>
    public bool XySwap { get; init; }

    /// <summary>
    /// 反查的 XY 對調旗標（<c>TGOS_GEO_XY_SWAP</c>），與正查的 <see cref="XySwap"/> **分開**：
    /// 兩支是不同服務、不同端點，沒有理由假設它們對 X/Y 的定義一定一致。
    /// true 時**送出**的 <c>oPX</c> 放緯度、<c>oPY</c> 放經度，**回應**裡的 X 也當緯度解。
    /// 預設 false＝X 經度、Y 緯度（EPSG:4326 的定義）。
    /// ⚠️ 核准前拿不到真金鑰，這個假設**未實測**；拿到金鑰後第一件事就是驗它（台灣的經度 ≈121、緯度 ≈25，一眼可辨）。
    /// </summary>
    public bool GeoXySwap { get; init; }

    /// <summary>兩次打 TGOS 的最小間隔（毫秒）。TGOS 未公布配額，且使用規定第七條第 5 款禁短時間大量查詢，預設 1000。</summary>
    public int MinIntervalMs { get; init; } = 1000;

    /// <summary>
    /// 正查（地址→坐標）端點。官方現行版本是 <c>v40</c>；
    /// <c>v30</c> 與舊網域 <c>addr.tgos.nat.gov.tw</c> 都是舊值。
    /// v40 的 QueryAddr 多了 <c>oIsSupportHistory</c>／<c>oIsShowCodeBase</c> 兩個**選用**參數，
    /// 不送也相容，所以 <see cref="TgosClient"/> 的參數集合不必跟著改。
    /// </summary>
    public const string 預設TgosUrl = "https://addr.tgos.tw/addrws/v40/QueryAddr.asmx/QueryAddr";

    /// <summary>
    /// 反查（坐標→門牌）端點：坐標回傳門牌服務的「點查最近門牌」。
    /// 同服務另有 <c>PointQueryAddr</c>（多一個 <c>oBuffer</c> 公尺參數）、<c>PointQueryNearAddrByBuffer</c>、
    /// <c>LineQueryAddr</c>、<c>PolygonQueryAddr</c>；TAG 追蹤要的是「離這個 GPS 點最近的門牌」，所以用 NearAddr。
    /// </summary>
    public const string 預設TgosGeoUrl = "https://addr.tgos.tw/addrws/v40/GeoQueryAddr.asmx/PointQueryNearAddr";

    public bool RelayKey已設 => !string.IsNullOrWhiteSpace(RelayKey);
    public bool Tgos金鑰已設 => !string.IsNullOrWhiteSpace(TgosAppId) && !string.IsNullOrWhiteSpace(TgosApiKey);
    public bool Geo金鑰已設 => !string.IsNullOrWhiteSpace(TgosGeoAppId) && !string.IsNullOrWhiteSpace(TgosGeoApiKey);

    public static RelayConfig FromEnvironment()
    {
        static string E(string k) => (Environment.GetEnvironmentVariable(k) ?? "").Trim();
        var url = E("TGOS_URL");
        var geoUrl = E("TGOS_GEO_URL");
        return new RelayConfig
        {
            RelayKey = E("RELAY_KEY"),
            TgosAppId = E("TGOS_APPID"),
            TgosApiKey = E("TGOS_APIKEY"),
            TgosUrl = url.Length == 0 ? 預設TgosUrl : url,
            TgosGeoAppId = E("TGOS_GEO_APPID"),
            TgosGeoApiKey = E("TGOS_GEO_APIKEY"),
            TgosGeoUrl = geoUrl.Length == 0 ? 預設TgosGeoUrl : geoUrl,
            XySwap = string.Equals(E("TGOS_XY_SWAP"), "true", StringComparison.OrdinalIgnoreCase),
            GeoXySwap = string.Equals(E("TGOS_GEO_XY_SWAP"), "true", StringComparison.OrdinalIgnoreCase),
            MinIntervalMs = int.TryParse(E("TGOS_MIN_INTERVAL_MS"), out var ms) && ms >= 0 ? ms : 1000,
        };
    }
}
