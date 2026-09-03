using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TS.API.ExtData.Geocode;

/// <summary>
/// OSM 官方 Nominatim 公用實例。使用政策（https://operations.osmfoundation.org/policies/nominatim/）硬性條款：
/// ① 絕對上限 1 request/second ② 必須帶可識別應用的 User-Agent（不可用函式庫預設值）
/// ③ 結果必須自行快取（由 <see cref="GeocodeCache"/> ＋ extdata.geocode_cache 滿足）
/// ④ 對商業應用有「存取可能隨時被撤銷」警語——正式上線要改 TGOS。
///
/// 邏輯抄自 ERPV2 <c>TsErp.Geo/Geocoding/NominatimGeocodeProvider.cs</c>（含降級查詢），
/// **刻意複製而不是引用**：TSAPI 不參考 ERPV2 組件，兩邊各自部署、版本不必連動。
///
/// ⚠️ 節流的界線：閘門是本行程內的 static，Functions **多實例時各算各的**，
///    整體仍可能超過 1 req/s。現階段流量極小（拜訪行程規劃，一天數十筆且大多命中快取），
///    可接受；換 TGOS 之後這個限制自然消失。
/// </summary>
public sealed class NominatimBackend : IGeocodeBackend
{
    /// <summary>可識別本應用的 User-Agent。缺這個 Nominatim 會直接回 403。</summary>
    public const string 預設UserAgent = "TsERP-Central-Geocode/1.0 (contact: momo16542@gmail.com)";

    public const string 預設BaseUrl = "https://nominatim.openstreetmap.org";

    /// <summary>門牌命中。判定依據見 <see cref="GeoPrecision"/>（不是「輸入有門牌號」，是回應物件的層級）。</summary>
    public const string 門牌精度 = GeoPrecision.門牌;

    /// <summary>
    /// 非門牌精度的標記。**2026-09-02 實測**：Nominatim 對台灣完整門牌命中率很差
    /// （「台北市信義區市府路1號」回空陣列，去掉門牌號的「台北市信義區市府路」才查得到），
    /// 所以完整地址查無時自動退一階查到「路/街」層級，並把精度標成這個值，
    /// 讓客戶端畫面看得出來這不是門牌精度。要門牌精度請改用 TGOS。
    ///
    /// 除了「退一階查到的」以外，**完整地址查到、但回來的物件不是門牌層級**
    /// （例如「…市府路1號」回的是 <c>addresstype=office</c> 的臺北市政府）也標這個值——
    /// 判定一律走 <see cref="GeoPrecision"/>（2026-09-03 審查 S1）。
    /// </summary>
    public const string 降級精度 = GeoPrecision.路名;

    /// <summary>台灣地址的門牌尾段：「123號」「123之4號」以及其後的樓層/室。</summary>
    private static readonly Regex 門牌尾段 = new(@"\d+\s*(之\s*\d+)?\s*號.*$", RegexOptions.Compiled);

    /// <summary>
    /// 共用的長壽命 HttpClient。每次呼叫各 new 一個且不 Dispose 會累積 socket handle，
    /// 逾時改用每次請求的 CancellationTokenSource 控制，所以這裡不設 Timeout。
    /// </summary>
    private static readonly HttpClient _http = new();

    // 1 req/s 節流：閘門與「上次送出時間」都是 static，同一行程內所有呼叫共用一條隊伍。
    private static readonly SemaphoreSlim _閘 = new(1, 1);
    private static DateTime _上次送出 = DateTime.MinValue;

    private static readonly TimeSpan 最小間隔 = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan 逾時 = TimeSpan.FromSeconds(10);

    private readonly string _base;
    private readonly string _userAgent;

    public NominatimBackend(string? baseUrl = null, string? userAgent = null)
    {
        _base = (baseUrl ?? Environment.GetEnvironmentVariable("GEOCODE_NOMINATIM_URL") ?? 預設BaseUrl).TrimEnd('/');
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? 預設UserAgent : userAgent!;
    }

    public string 來源 => "NOMINATIM";

    public async Task<GeocodeHit?> GeocodeAsync(string 地址, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(地址)) return null;
        var 原地址 = 地址.Trim();

        var 降級地址 = 去掉門牌號(原地址);

        // 精度只有 house／road-fallback 兩種值（API 合約），要標得誠實，所以兩道關卡都要過：
        //   ① 這一趟查詢有沒有資格拿門牌精度——地址**本身就沒有門牌號**時（「…航站南路」），
        //      命中的本來就是路名層級，不能因為第一次就查到而標成 house。
        //   ② 回來的物件是不是門牌／建物層級（GeoPrecision.判定 讀 addresstype）——
        //      「市府路1號」很常回一筆 office 的機關物件，那不是門牌。
        var hit = await 查一次(原地址, 允許門牌: 降級地址 != null, ct).ConfigureAwait(false);
        if (hit != null) return hit;

        if (降級地址 == null) return null;   // 本來就沒門牌號＝沒有更粗的一階可退

        return await 查一次(降級地址, 允許門牌: false, ct).ConfigureAwait(false);
    }

    /// <summary>把「…路1號3樓」縮成「…路」。已經沒有門牌號、或縮完是空的就回 null（＝不必再查一次）。</summary>
    public static string? 去掉門牌號(string? 地址)
    {
        if (string.IsNullOrWhiteSpace(地址)) return null;
        var s = 門牌尾段.Replace(地址!.Trim(), string.Empty).Trim();
        if (s.Length == 0) return null;
        return string.Equals(s, 地址.Trim(), StringComparison.Ordinal) ? null : s;
    }

    private async Task<GeocodeHit?> 查一次(string 地址, bool 允許門牌, CancellationToken ct)
    {
        var url = _base + "/search?q=" + Uri.EscapeDataString(地址)
                + "&format=jsonv2&countrycodes=tw&limit=1";

        await 等到可以送出(ct).ConfigureAwait(false);

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
                // 403 多半是 User-Agent 被拒或被限流；一律當上游故障（不是「查無」）
                throw new GeocodeBackendException(
                    $"Nominatim 定位「{地址}」回 HTTP {(int)res.StatusCode}：{截短(body)}");
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
            throw new GeocodeBackendException($"Nominatim 定位「{地址}」失敗：{ex.Message}", ex);
        }

        var p = 解析(body, 來源, 允許門牌);
        return p;
    }

    /// <summary>
    /// 把 jsonv2 回應解析成座標。抽成靜態方法讓治具能餵罐頭 JSON。查無資料回 null。
    /// 精度不是呼叫端指定的，是從回應的 <c>addresstype</c>（退 <c>type</c>、<c>category</c>）
    /// 交給 <see cref="GeoPrecision.判定"/> 決定——理由見該類別。
    /// </summary>
    /// <param name="允許門牌">false＝這一趟查的是降級地址（或輸入本來就沒門牌號），一律不給門牌精度。</param>
    public static GeocodeHit? 解析(string? json, string 來源, bool 允許門牌 = true)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json!);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        foreach (var it in doc.RootElement.EnumerateArray())
        {
            // jsonv2 的 lat／lon 是字串型別
            if (!取數字(it, "lat", out var lat)) continue;
            if (!取數字(it, "lon", out var lon)) continue;

            var 物件層級 = 取字串(it, "addresstype") ?? 取字串(it, "type") ?? 取字串(it, "category");
            return new GeocodeHit(lon, lat, 來源, GeoPrecision.判定(物件層級, 允許門牌));
        }
        return null;
    }

    private static async Task 等到可以送出(CancellationToken ct)
    {
        await _閘.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var 距上次 = DateTime.UtcNow - _上次送出;
            if (距上次 < 最小間隔)
            {
                await Task.Delay(最小間隔 - 距上次, ct).ConfigureAwait(false);
            }
            _上次送出 = DateTime.UtcNow;
        }
        finally
        {
            _閘.Release();
        }
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

    private static string? 取字串(JsonElement e, string 名)
        => e.TryGetProperty(名, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string 截短(string? s)
        => string.IsNullOrEmpty(s) ? "" : (s!.Length <= 200 ? s : s[..200] + "…");
}
