namespace TgosRelay;

/// <summary>上游故障（連不上、逾時、非 2xx、回應不是 JSON）。與「查無」分開：查無是正常結果（found=false），故障要回 502 讓 YYAPI 知道「這次不算數」。</summary>
public sealed class TgosUpstreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 打 TGOS 的兩支服務（都是 POST form）：
/// <list type="bullet">
///   <item>正查 <c>QueryAddr</c>（地址→坐標）：參數組合與 ERPV2 <c>TgosGeocodeProvider</c> 相同——
///         EPSG:4326、模糊比對、只回 1 筆、可忽略村里／鄰。</item>
///   <item>反查 <c>GeoQueryAddr/PointQueryNearAddr</c>（坐標→最近門牌）。</item>
/// </list>
/// 兩支是**各自申請的服務**，金鑰各一組（<see cref="RelayConfig.TgosGeoAppId"/> 的說明），不可互用。
/// </summary>
public sealed class TgosClient(HttpClient http, RelayConfig cfg)
{
    public async Task<TgosHit?> QueryAsync(string 地址, CancellationToken ct)
    {
        var 表單 = new Dictionary<string, string>
        {
            ["oAPPId"] = cfg.TgosAppId,
            ["oAPIKey"] = cfg.TgosApiKey,
            ["oAddress"] = 去郵遞區號(地址),
            ["oSRS"] = "EPSG:4326",
            ["oFuzzyType"] = "2",
            ["oResultDataType"] = "JSON",
            ["oFuzzyBuffer"] = "0",
            ["oIsOnlyFullMatch"] = "false",
            // v40 新增、**必送**的兩個參數：不送 TGOS 直接回 HTTP 500「缺少參數: oIsSupportPast」
            // （2026-09-09 拿到金鑰後第一筆正查就中——v30 沒有這兩個，升 v40 時漏補；
            //  這種錯只有真金鑰打得出來，罐頭測試看不到）。
            ["oIsSupportPast"] = "false",   // 不查舊門牌：要的是現行門牌，舊門牌會帶回已裁撤的地址
            ["oIsShowCodeBase"] = "false",  // 不要統計區資訊，維持最小回應
            ["oIsLockCounty"] = "false",
            ["oIsLockTown"] = "false",
            ["oIsLockVillage"] = "false",
            ["oIsLockRoadSection"] = "false",
            ["oIsLockLane"] = "false",
            ["oIsLockAlley"] = "false",
            ["oIsLockArea"] = "false",
            ["oIsSameNumber_SubNumber"] = "true",
            ["oCanIgnoreVillage"] = "true",
            ["oCanIgnoreNeighborhood"] = "true",
            ["oReturnMaxCount"] = "1",
        };

        var body = await 送出Async(cfg.TgosUrl, 表單, ct).ConfigureAwait(false);

        try
        {
            return TgosResponseParser.Parse(body, cfg.XySwap);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // TGOS 金鑰錯／IP 未登記時常回一段說明文字而不是 JSON——當上游故障處理，訊息帶前 200 字給維運看
            throw new TgosUpstreamException($"TGOS 回應不是 JSON：{截短(body)}", ex);
        }
    }

    /// <summary>
    /// 反查：坐標 → 最近門牌（<c>PointQueryNearAddr</c>）。
    ///
    /// <c>oPX</c>／<c>oPY</c> 是**平面坐標的 X／Y**，在 <c>oSRS=EPSG:4326</c> 下就是經度／緯度
    /// （X＝經度、Y＝緯度），所以不需要做 TWD97 轉換，WGS84 直接送。
    /// 這個 X/Y 對應**核准前未實測**，實測發現相反就把 <c>TGOS_GEO_XY_SWAP</c> 設 true
    /// （送出與解析同時翻轉，見 <see cref="RelayConfig.GeoXySwap"/>）。
    ///
    /// 座標一律 <see cref="System.Globalization.CultureInfo.InvariantCulture"/> 格式化：
    /// VM 的地區設定若不是英文（或將來換機器），預設格式化會把小數點寫成逗號，
    /// "25,0377" 送出去不會炸，只會查無資料——最難查的那種錯。
    /// </summary>
    public async Task<TgosReverseHit?> ReverseAsync(double lat, double lon, CancellationToken ct)
    {
        var px = cfg.GeoXySwap ? lat : lon;
        var py = cfg.GeoXySwap ? lon : lat;
        var 表單 = new Dictionary<string, string>
        {
            // ⚠️ 反查用的是**另一組**金鑰（坐標回傳門牌服務），不是正查那組
            ["oAPPId"] = cfg.TgosGeoAppId,
            ["oAPIKey"] = cfg.TgosGeoApiKey,
            ["oPX"] = px.ToString("F8", System.Globalization.CultureInfo.InvariantCulture),
            ["oPY"] = py.ToString("F8", System.Globalization.CultureInfo.InvariantCulture),
            ["oSRS"] = "EPSG:4326",
            ["oResultDataType"] = "JSON",
            ["oIsShowCodeBase"] = "false",
        };

        var body = await 送出Async(cfg.TgosGeoUrl, 表單, ct).ConfigureAwait(false);
        // 非 JSON（例：金鑰錯回的 "Length of the data to decrypt is invalid."）由 parser 丟 TgosUpstreamException
        return TgosReverseParser.Parse(body, cfg.GeoXySwap, cfg.GeoIncludeVillage);
    }

    /// <summary>POST form 並取回本文；連線／逾時／非 2xx 一律轉成 <see cref="TgosUpstreamException"/>。</summary>
    private async Task<string> 送出Async(string url, Dictionary<string, string> 表單, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(表單) };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new TgosUpstreamException($"TGOS 回 HTTP {(int)resp.StatusCode}：{截短(body)}");
            return body;
        }
        catch (TgosUpstreamException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TgosUpstreamException("TGOS 逾時");
        }
        catch (HttpRequestException ex)
        {
            throw new TgosUpstreamException($"TGOS 連線失敗：{ex.Message}", ex);
        }
    }

    private static string 截短(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "…";

    /// <summary>
    /// 去掉地址開頭的郵遞區號（3／5／6 碼）。
    ///
    /// **為什麼一定要做**（2026-09-09 用禾久真實客戶地址實測）：TGOS 的 QueryAddr **不吃**開頭的郵遞區號，
    /// 帶著就查無，拿掉同一個地址立刻命中門牌：
    ///   「231新北市新店區中興路3段3號9樓」→ 查無；「新北市新店區中興路3段3號9樓」→ 門牌命中。
    /// 禾久快取裡 5 筆有 3 筆是這種寫法（ERP 主檔地址帶郵遞區號很常見），不處理的話切成 TGOS 之後
    /// 這些客戶會直接從行程規劃的地圖上消失——**比原本的路名精度更糟**。
    /// （樓層不必處理：「…3號9樓」TGOS 自己會忽略，實測命中。）
    ///
    /// 只砍**開頭**且後面接非數字的那一段：地址中間的數字（巷弄號）不能碰。
    /// </summary>
    public static string 去郵遞區號(string 地址)
    {
        var s = (地址 ?? string.Empty).TrimStart();
        var i = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        // 台灣郵遞區號是 3／5／6 碼；長度不對就不是郵遞區號（可能是門牌開頭的怪寫法），原樣送出
        if (i is 3 or 5 or 6 && i < s.Length) return s.Substring(i).TrimStart();
        return 地址 ?? string.Empty;
    }
}
