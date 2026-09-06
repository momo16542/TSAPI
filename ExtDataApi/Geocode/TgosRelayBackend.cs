using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TS.API.ExtData.Geocode;

/// <summary>
/// 設定缺漏／設定錯（不是暫時性故障，重試不會好）。<see cref="GeocodeFunctions"/> 把它與
/// <see cref="NotSupportedException"/> 同一路處理：回 500、本文指名設定鍵（2026-09-03 審查 S4 的原則）。
/// </summary>
public sealed class GeocodeConfigurationException : Exception
{
    public GeocodeConfigurationException(string message) : base(message) { }
}

/// <summary>
/// TGOS 後端（2026-09-06）：**不直接打 TGOS**，打 GCP asia-east1 上的轉發器 <c>tgos-relay</c>
/// （repo 內 <c>TgosRelay/</c>；台灣固定 IP 34.80.181.162）。
/// 理由：TGOS 使用規定第七條第 2 款只接受中華民國 IP，而 YYAPI 在 Azure East Asia（香港），
/// 且 Flex Consumption 出站 IP 會漂、登記不了。TGOS 金鑰只放 VM，YYAPI 拿不到。
///
/// app settings：
///   TGOS_RELAY_URL  轉發器 base URL（例 https://tgos-relay.yanyue.io，尾斜線可有可無）
///   TGOS_RELAY_KEY  與 VM /etc/tgos-relay.env 的 RELAY_KEY 相同（放 Key Vault 參照）
///
/// 精度：TGOS 是門牌權威資料，但**核准前拿不到真實回應**，MATCH_TYPE 的詞彙未知。
/// 第一版判定寫在 <see cref="判定精度"/>：轉發器回的比對地址含「號」＝ house，否則 road-fallback；
/// 核准後（計畫 S5）依實際 MATCH_TYPE 值域收斂，並補進 <see cref="GeoPrecision"/> 的註解。
/// </summary>
public sealed class TgosRelayBackend : IGeocodeBackend
{
    public const string 設定鍵_Url = "TGOS_RELAY_URL";
    public const string 設定鍵_Key = "TGOS_RELAY_KEY";

    /// <summary>共用的長壽命 HttpClient（同 NominatimBackend 的理由）；逾時由每次請求的 CTS 控制。</summary>
    private static readonly HttpClient _http = new();
    private static readonly TimeSpan 逾時 = TimeSpan.FromSeconds(15); // 轉發器自己還要排 1 req/s 隊＋打 TGOS 10 秒

    private readonly string _url;
    private readonly string _key;

    public TgosRelayBackend(string? url = null, string? key = null)
    {
        _url = (url ?? Environment.GetEnvironmentVariable(設定鍵_Url) ?? string.Empty).Trim().TrimEnd('/');
        _key = (key ?? Environment.GetEnvironmentVariable(設定鍵_Key) ?? string.Empty).Trim();
        if (_url.Length == 0 || _key.Length == 0)
        {
            throw new GeocodeConfigurationException(
                $"GEOCODE_BACKEND=TGOS 需要 app setting {設定鍵_Url} 與 {設定鍵_Key}（目前缺：" +
                string.Join("、", new[] { _url.Length == 0 ? 設定鍵_Url : null, _key.Length == 0 ? 設定鍵_Key : null }.Where(s => s != null)) + "）");
        }
    }

    public string 來源 => "TGOS";

    public async Task<GeocodeHit?> GeocodeAsync(string 地址, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(逾時);

        string body;
        HttpStatusCode code;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                _url + "/geocode?address=" + Uri.EscapeDataString(地址));
            req.Headers.TryAddWithoutValidation("X-Relay-Key", _key);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            code = resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeocodeBackendException("TGOS 轉發器逾時");
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeBackendException("TGOS 轉發器連線失敗：" + ex.Message, ex);
        }

        return 解析回應(code, body);
    }

    /// <summary>
    /// 轉發器的狀態碼對映：200 正常；401／503 是**設定錯**（金鑰不對、VM 上 TGOS 金鑰沒填）→ 設定例外；
    /// 502 與其他一律上游故障 → 502 給 ERP「稍後再試」。抽成靜態方法讓測試餵罐頭資料。
    /// </summary>
    public static GeocodeHit? 解析回應(HttpStatusCode code, string? body)
    {
        var 訊息 = 取錯誤訊息(body);
        switch (code)
        {
            case HttpStatusCode.OK:
                break;
            case HttpStatusCode.Unauthorized:
                throw new GeocodeConfigurationException($"{設定鍵_Key} 與轉發器的 RELAY_KEY 不一致（轉發器回 401：{訊息}）");
            case HttpStatusCode.ServiceUnavailable:
                throw new GeocodeConfigurationException($"轉發器設定未完成（回 503：{訊息}）");
            default:
                throw new GeocodeBackendException($"TGOS 轉發器回 HTTP {(int)code}：{訊息}");
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body!); }
        catch (JsonException ex) { throw new GeocodeBackendException("TGOS 轉發器回應不是 JSON", ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new GeocodeBackendException("TGOS 轉發器回應格式不對");
            if (!root.TryGetProperty("found", out var f) || f.ValueKind != JsonValueKind.True) return null;
            if (!取數字(root, "lon", out var lon) || !取數字(root, "lat", out var lat))
                throw new GeocodeBackendException("TGOS 轉發器回 found=true 但缺 lon/lat");

            var 比對地址 = root.TryGetProperty("tgosAddress", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var matchType = root.TryGetProperty("matchType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return new GeocodeHit(lon, lat, "TGOS", 判定精度(比對地址, matchType));
        }
    }

    /// <summary>
    /// 第一版精度判定（核准後依真實 MATCH_TYPE 值域收斂）：
    /// 轉發器回的「TGOS 比對到的地址」本身含門牌「號」＝門牌層級 → house；否則 road-fallback。
    /// 看的是**回應的地址**不是輸入（同 GeoPrecision 的原則：輸入有門牌號不代表回來的是門牌）。
    /// 沒有比對地址欄位時一律 road-fallback（寧可低報）。
    /// </summary>
    public static string 判定精度(string? 比對地址, string? matchType)
    {
        if (!string.IsNullOrWhiteSpace(比對地址) && 比對地址!.Contains('號')) return GeoPrecision.門牌;
        return GeoPrecision.路名;
    }

    private static string 取錯誤訊息(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(空)";
        try
        {
            using var doc = JsonDocument.Parse(body!);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString() ?? "(空)";
        }
        catch (JsonException) { }
        return body!.Length <= 200 ? body! : body!.Substring(0, 200) + "…";
    }

    private static bool 取數字(JsonElement e, string 名, out double 值)
    {
        值 = 0;
        if (!e.TryGetProperty(名, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out 值),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out 值),
            _ => false,
        };
    }
}
