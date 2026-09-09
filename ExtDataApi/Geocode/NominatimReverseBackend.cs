using System.Globalization;
using System.Text.Json;

namespace TS.API.ExtData.Geocode;

/// <summary>
/// OSM 官方 Nominatim 的**反查**（座標 → 地址）端點 <c>/reverse</c>。
/// 使用政策（https://operations.osmfoundation.org/policies/nominatim/）硬性條款與正查完全相同：
/// ① 絕對上限 1 request/second——**與正查加總計算**，所以節流走共用的
///    <see cref="NominatimThrottle"/>，不是這支自己一份（各自一份就是 2 req/s）。
/// ② 必須帶可識別應用的 User-Agent（沿用 <see cref="NominatimBackend.預設UserAgent"/>，
///    同一個應用不該對上游宣稱成兩個）。
/// ③ 結果必須自行快取（由 <see cref="ReverseGeocodeCache"/> ＋ extdata.reverse_geocode_cache 滿足）。
/// ④ 對商業應用有「存取可能隨時被撤銷」警語——正式上線要改 TGOS（待轉發器 /reverse）。
///
/// 用途（2026-09-09）：SH03A 的 TAG 追蹤匯入手持機 LOG 時，GPS 一定有、施工地址常常空白，
/// 用座標把地址補回來。同一個工地會有幾十支 TAG 掃在幾公尺內，所以格網化快取是必需的，
/// 不是最佳化——見 <see cref="ReverseGeocodeCache.座標鍵"/>。
/// </summary>
public sealed class NominatimReverseBackend : IReverseGeocodeBackend
{
    public const string 預設BaseUrl = NominatimBackend.預設BaseUrl;

    /// <summary>
    /// 反查的 <c>zoom</c>：18＝建物／門牌層級（Nominatim 的 zoom 值域 0–18，越大越細）。
    /// 給 18 不代表一定回門牌——回應層級由 <see cref="GeoPrecision"/> 判，
    /// 這裡只是「要求上游盡量細」；給小一點會直接拿不到門牌，連判都不用判。
    /// </summary>
    public const int 預設Zoom = 18;

    /// <summary>共用的長壽命 HttpClient（同 <see cref="NominatimBackend"/> 的理由）；逾時由每次請求的 CTS 控制。</summary>
    private static readonly HttpClient _http = new();

    private static readonly TimeSpan 逾時 = TimeSpan.FromSeconds(10);

    private readonly string _base;
    private readonly string _userAgent;
    private readonly int _zoom;

    public NominatimReverseBackend(string? baseUrl = null, string? userAgent = null, int? zoom = null)
    {
        // 沿用正查的 GEOCODE_NOMINATIM_URL：同一個上游主機，兩支端點沒有理由指到不同地方
        _base = (baseUrl ?? Environment.GetEnvironmentVariable("GEOCODE_NOMINATIM_URL") ?? 預設BaseUrl).TrimEnd('/');
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? NominatimBackend.預設UserAgent : userAgent!;
        _zoom = zoom ?? 預設Zoom;
    }

    public string 來源 => "NOMINATIM";

    public async Task<ReverseHit?> ReverseAsync(double lat, double lon, CancellationToken ct = default)
    {
        // 座標一律 InvariantCulture 格式化：在 zh-TW 之外的地區設定（例如 de-DE）下，
        // 預設格式化會把小數點寫成逗號，"25,0377" 送給上游就是一個語法錯的請求——
        // 而且不會炸，只會查無資料（審查最難發現的那種）。F6 ≈ 0.1 公尺，反查夠用。
        var url = _base + "/reverse?format=jsonv2"
                + "&lat=" + lat.ToString("F6", CultureInfo.InvariantCulture)
                + "&lon=" + lon.ToString("F6", CultureInfo.InvariantCulture)
                + "&accept-language=zh-TW"
                + "&zoom=" + _zoom.ToString(CultureInfo.InvariantCulture);

        await NominatimThrottle.等到可以送出(ct).ConfigureAwait(false);

        using var 逾時來源 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        逾時來源.CancelAfter(逾時);

        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // TryAddWithoutValidation：UA 內含括號與冒號，嚴格驗證會拒收
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-TW,zh");

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, 逾時來源.Token)
                .ConfigureAwait(false);
            body = await res.Content.ReadAsStringAsync(逾時來源.Token).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                // 403 多半是 User-Agent 被拒或被限流；一律當上游故障（不是「查無」）——
                // 混成查無的話，上游掛掉會被整批快取成「這些座標都查不到地址」。
                throw new GeocodeBackendException(
                    $"Nominatim 反查 ({lat.ToString("F6", CultureInfo.InvariantCulture)}," +
                    $"{lon.ToString("F6", CultureInfo.InvariantCulture)}) 回 HTTP {(int)res.StatusCode}：{截短(body)}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 呼叫端主動取消，往上丟
        }
        catch (GeocodeBackendException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GeocodeBackendException($"Nominatim 反查失敗：{ex.Message}", ex);
        }

        return 解析(body, 來源);
    }

    /// <summary>
    /// 把 <c>/reverse</c> 的 jsonv2 回應解析成地址。抽成靜態方法讓治具能餵罐頭 JSON。
    ///
    /// 與正查的差別：<c>/reverse</c> 回的是**單一物件**（不是陣列），查無時回
    /// <c>{"error":"Unable to geocode"}</c> 而不是空陣列——這是「查無」不是故障
    /// （上游有回 HTTP 200，它只是說這個座標沒有對應的地址），所以回 null，
    /// 由端點回 found=false。
    ///
    /// 精度一律走 <see cref="GeoPrecision.判定"/>，依據是回應的 <c>addresstype</c>
    /// （退 <c>type</c>、<c>category</c>），不是「地址字串裡有沒有『號』」——理由見 <see cref="GeoPrecision"/>。
    /// 反查沒有正查那種「退一階再查」的降級，所以 允許門牌 恆為 true：
    /// 是不是門牌完全由回應物件的層級決定。
    /// </summary>
    public static ReverseHit? 解析(string? json, string 來源)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json!); }
        catch (JsonException ex) { throw new GeocodeBackendException("Nominatim 反查回應不是 JSON", ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // {"error":"Unable to geocode"}＝查無（上游回 200），不是故障
            if (root.TryGetProperty("error", out _)) return null;

            var 地址 = 取字串(root, "display_name");
            if (string.IsNullOrWhiteSpace(地址)) return null;   // 沒有地址字串＝這筆對呼叫端沒有用處

            var 物件層級 = 取字串(root, "addresstype") ?? 取字串(root, "type") ?? 取字串(root, "category");
            return new ReverseHit(地址!.Trim(), 來源, GeoPrecision.判定(物件層級));
        }
    }

    private static string? 取字串(JsonElement e, string 名)
        => e.TryGetProperty(名, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string 截短(string? s)
        => string.IsNullOrEmpty(s) ? "" : (s!.Length <= 200 ? s : s[..200] + "…");
}
