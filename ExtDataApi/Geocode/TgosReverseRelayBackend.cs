using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TS.API.ExtData.Geocode;

/// <summary>
/// TGOS **反查**後端（2026-09-09）：與正查 <see cref="TgosRelayBackend"/> 一樣不直接打 TGOS，
/// 打的是 GCP asia-east1 上的轉發器 <c>tgos-relay</c> 的 <c>/reverse</c>
/// （TGOS 使用規定第七條第 2 款只接受中華民國 IP，YYAPI 在 Azure East Asia 出不去；理由詳見正查那支）。
///
/// app settings 沿用**同一組**（同一台轉發器、同一把共用密鑰，沒有理由分兩份設定）：
///   TGOS_RELAY_URL  轉發器 base URL
///   TGOS_RELAY_KEY  與 VM /etc/tgos-relay.env 的 RELAY_KEY 相同
/// ⚠️ 轉發器**內部**打 TGOS 用的是另一組金鑰（TGOS_GEO_APPID／TGOS_GEO_APIKEY，坐標回傳門牌服務要另外申請），
///    那組只在 VM 上，YYAPI 拿不到也不需要知道。轉發器沒填那組時會回 503 → 這裡轉成設定例外。
///
/// 精度：與正查同樣的處境——核准前拿不到真回應，反查回應的欄位名與 match/score 值域都未知。
/// 第一版沿用正查的啟發式（見 <see cref="判定精度"/>），核准後依實際值域收斂。
/// </summary>
public sealed class TgosReverseRelayBackend : IReverseGeocodeBackend
{
    /// <summary>與正查共用的設定鍵：同一台轉發器。</summary>
    public const string 設定鍵_Url = TgosRelayBackend.設定鍵_Url;
    public const string 設定鍵_Key = TgosRelayBackend.設定鍵_Key;

    /// <summary>共用的長壽命 HttpClient（同 <see cref="TgosRelayBackend"/> 的理由）；逾時由每次請求的 CTS 控制。</summary>
    private static readonly HttpClient _http = new();
    private static readonly TimeSpan 逾時 = TimeSpan.FromSeconds(15); // 轉發器自己還要排 1 req/s 隊＋打 TGOS 10 秒

    private readonly string _url;
    private readonly string _key;

    public TgosReverseRelayBackend(string? url = null, string? key = null)
    {
        _url = (url ?? Environment.GetEnvironmentVariable(設定鍵_Url) ?? string.Empty).Trim().TrimEnd('/');
        _key = (key ?? Environment.GetEnvironmentVariable(設定鍵_Key) ?? string.Empty).Trim();
        if (_url.Length == 0 || _key.Length == 0)
        {
            throw new GeocodeConfigurationException(
                $"{ReverseGeocodeBackendFactory.設定鍵}=TGOS 需要 app setting {設定鍵_Url} 與 {設定鍵_Key}（目前缺：" +
                string.Join("、", new[] { _url.Length == 0 ? 設定鍵_Url : null, _key.Length == 0 ? 設定鍵_Key : null }.Where(s => s != null)) + "）");
        }
    }

    public string 來源 => "TGOS";

    public async Task<ReverseHit?> ReverseAsync(double lat, double lon, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(逾時);

        // 座標一律 InvariantCulture（同 NominatimReverseBackend）：de-DE 下預設格式化會寫出 "25,0377"，
        // 那不會炸，只會查無資料。F6 ≈ 0.1 公尺，反查夠用。
        var url = _url + "/reverse?lat=" + lat.ToString("F6", CultureInfo.InvariantCulture)
                       + "&lon=" + lon.ToString("F6", CultureInfo.InvariantCulture);

        string body;
        HttpStatusCode code;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
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
    /// 轉發器的狀態碼對映（與正查完全一致）：200 正常；401／503 是**設定錯**
    /// （共用密鑰不對、VM 上 TGOS_GEO_APPID／APIKEY 沒填）→ 設定例外；其他一律上游故障 → 502。
    /// 抽成靜態方法讓測試餵罐頭資料。
    /// </summary>
    public static ReverseHit? 解析回應(HttpStatusCode code, string? body)
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

            var 地址 = root.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            // found=true 卻沒有地址＝轉發器或上游講不通（不是「查無」，查無會回 found=false）
            if (string.IsNullOrWhiteSpace(地址))
                throw new GeocodeBackendException("TGOS 轉發器回 found=true 但缺 address");

            var matchType = root.TryGetProperty("matchType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return new ReverseHit(地址!.Trim(), "TGOS", 判定精度(地址, matchType));
        }
    }

    /// <summary>
    /// 第一版精度判定（沿用正查 <see cref="TgosRelayBackend.判定精度"/> 的啟發式）：
    /// 反查回來的地址含門牌「號」＝門牌層級 → house；否則 road-fallback。
    ///
    /// 這裡看地址字串**不違反** <see cref="GeoPrecision"/>「要看物件層級」的原則的理由與正查相同：
    /// TGOS 的坐標回傳門牌服務回的**本來就是門牌資料集**，字串裡沒有「號」代表它連門牌都沒對到；
    /// 而它的 match/score 值域目前未知（規格書沒有回應範例，核准前打不出真回應）。
    /// 拿到真金鑰後（計畫 S5）依實際值域收斂，屆時這個字串啟發式退成 fallback。
    /// 沒有地址或沒有「號」一律 road-fallback（寧可低報，同 GeoPrecision 的原則）。
    /// </summary>
    public static string 判定精度(string? 地址, string? matchType)
    {
        if (!string.IsNullOrWhiteSpace(地址) && 地址!.Contains('號')) return GeoPrecision.門牌;
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
}
