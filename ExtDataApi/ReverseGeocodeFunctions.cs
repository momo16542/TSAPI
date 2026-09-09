using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TS.API.ExtData.Geocode;

namespace TS.API.ExtData;

/// <summary>
/// 反查 API（座標 → 地址，2026-09-09）。與正查 <see cref="GeocodeFunctions"/> 對稱的另一半。
///
/// 用途：SH03A 的 TAG 追蹤系統匯入手持機 LOG 時，**GPS 一定有、施工地址常常空白**
/// （現場人員不會打字），用座標把地址補回來。
///
/// 為什麼一樣要集中在中央而不是各客戶自己打上游：
///   ① TGOS（之後要換的門牌權威來源）採 **IP 驗證**，只接受中華民國 IP，
///      金鑰放在 GCP 台灣 VM 的轉發器上——各客戶拿不到也不該拿到。
///   ② Nominatim 的 1 req/s 是所有呼叫端**加總**的上限，而且**正查反查共用**
///      （見 <see cref="NominatimThrottle"/>）；集中才管得住。
///   ③ 工地座標高度重疊（同一個工地幾十支 TAG），中央快取一次命中所有客戶都受惠。
///
/// 認證／授權／用量全部沿用既有那一套（<see cref="ApiPipeline"/>）：資料集名 <c>reverse-geocode</c>，
/// 只是 extdata.api_client_dataset 多一列，usp_api_key_verify 不用改。
///
/// **查無地址回 200 found=false，不是 404**（同正查）：查不到是正常結果——海上、山區、
/// 新開發區都可能沒有對應地址。用 4xx 表達會讓不重試的客戶端誤判成「服務壞了」，
/// 也讓用量統計分不出故障與查無。真的故障（上游連不上／回錯）才 502。
///
/// 回應的 <c>precision</c> 值域與正查相同（<c>house</c>／<c>road-fallback</c>），
/// 判定的唯一實作一樣在 <see cref="GeoPrecision"/>（看上游回應物件的層級）。
/// </summary>
public class ReverseGeocodeFunctions
{
    private const string Dataset = "reverse-geocode";

    private readonly ILogger _logger;

    public ReverseGeocodeFunctions(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<ReverseGeocodeFunctions>();

    /// <param name="ct">
    /// 呼叫端斷線時由 host 取消。一路傳到查快取／打上游／寫快取三處——
    /// 客戶端都走了還讓 Nominatim 的 1 req/s 預算被佔著，等於替沒人要的答案排隊。
    /// </param>
    [Function("ReverseGeocode")]
    public async Task<HttpResponseData> ReverseGeocode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/reverse-geocode")] HttpRequestData req,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var q = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var 參數 = req.Url.Query;

        var (caller, 拒絕) = await ApiPipeline.驗證Async(_logger, req, Dataset, 參數, sw);
        if (拒絕 is not null) return 拒絕;

        var (lat, lon, 參數錯誤) = 驗參數(q["lat"], q["lon"]);
        if (參數錯誤 is not null)
            return await 錯誤(req, caller!.ClientId, 參數, sw, HttpStatusCode.BadRequest, 參數錯誤);

        // 快取鍵用**格網化**後的座標（≈1 公尺一格，理由見 ReverseGeocodeCache.座標鍵）；
        // 回應給呼叫端的 lat/lon 一律是**原值**（傳什麼回什麼）——
        // 回格網化值會讓呼叫端以為自己送出的座標被系統「修正」過，
        // 而它送進來的是手持機的實測座標，我們沒有立場改它。
        var 鍵 = ReverseGeocodeCache.座標鍵(lat, lon);

        try
        {
            await using var cn = await CentralDb.OpenAsync();

            // 先取後端：快取命中與否要看「這筆是不是目前這個後端寫的」（換 TGOS 後舊列必須重查）。
            var backend = ReverseGeocodeBackendFactory.預設後端;

            var 快取 = await ReverseGeocodeCache.TryGetAsync(cn, 鍵, backend.來源, ct);
            // TGOS 結果的保留時數設 0＝完全不快取（GeocodeCache.找到保留時數），這時連寫都不寫
            if (快取 is not null)
            {
                await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數,
                    快取.找到 ? 1 : 0, sw.ElapsedMilliseconds, 200, "cache");
                return await 回應(req, lat, lon, 快取.找到, 快取.Address,
                    快取.Source, 快取.Precision, cached: true);
            }

            var hit = await backend.ReverseAsync(lat, lon, ct);

            // 查無也寫快取（找到=0）——否則一個查不到地址的座標每次都會打上游，
            // 而上游的 1 req/s 是所有客戶共用的。過期規則見 ReverseGeocodeCache.查無保留天數。
            if (ReverseGeocodeCache.可寫快取(backend.來源))
                await 寫快取(cn, 鍵, lat, lon, hit, backend.來源, ct);

            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數,
                hit is null ? 0 : 1, sw.ElapsedMilliseconds, 200, "backend:" + backend.來源);
            return await 回應(req, lat, lon, hit is not null, hit?.Address,
                hit?.Source ?? backend.來源, hit?.Precision, cached: false);
        }
        catch (GeocodeBackendException ex)
        {
            _logger.LogError(ex, "反查後端故障");
            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 502,
                ex.GetType().Name + " " + ex.Message);
            // 上游的錯誤內容不轉給呼叫端（含 URL 與金鑰的機會不小）
            return await ApiPipeline.Json(req, HttpStatusCode.BadGateway,
                new { error = "反查服務暫時無法使用" });
        }
        catch (Exception ex) when (ex is NotSupportedException or GeocodeConfigurationException)
        {
            // REVERSE_BACKEND 設定錯誤（未知的值，或現階段設成 TGOS——反查還沒有 TGOS 後端）。
            // 這是**設定問題，不會自己好**，而 ERP 端把 5xx 一律當「暫時性、稍後再試」，
            // 所以回應本文一定要指名是哪個設定錯了，否則維運者只能去 App Insights 撈 log
            // 才知道要改什麼（沿用正查 2026-09-03 審查 S4 的原則）。
            _logger.LogError(ex, "REVERSE_BACKEND 設定錯誤");
            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                "設定錯誤 " + ReverseGeocodeBackendFactory.設定鍵 + "：" + ex.Message);
            return await ApiPipeline.Json(req, HttpStatusCode.InternalServerError, new
            {
                error = $"伺服器設定錯誤：app setting {ReverseGeocodeBackendFactory.設定鍵} 的值無法建立反查後端"
                        + $"（{ex.Message}）。這不是暫時性故障，重試不會好，請通知提供方修正設定。",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "reverse-geocode 失敗");
            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                ex.GetType().Name + " " + ex.Message);
            return await ApiPipeline.Json(req, HttpStatusCode.InternalServerError,
                new { error = "查詢失敗，請聯絡提供方" });
        }
    }

    /// <summary>
    /// lat／lon 的驗證（純函式，抽出來讓治具直接測）。
    /// 回傳第三項為 null＝通過；否則是要回給呼叫端的 400 訊息。
    ///
    /// 訊息一律指名是哪個參數、什麼問題：呼叫端是 ERP 的匯入程式，
    /// 一句「參數錯誤」會讓現場只知道「匯入失敗」而查不下去。
    /// </summary>
    public static (double lat, double lon, string? 錯誤) 驗參數(string? latRaw, string? lonRaw)
    {
        var (lat, e1) = 解析座標(latRaw, "lat", 90);
        if (e1 is not null) return (0, 0, e1);

        var (lon, e2) = 解析座標(lonRaw, "lon", 180);
        if (e2 is not null) return (0, 0, e2);

        // **(0,0) 一律擋掉**：那是幾內亞灣海面上的一個真實座標，但在這個系統裡它只有一個來源——
        // 手持機「沒定位到」時把兩個欄位都留成 0。放行的話會反查出「大西洋」之類的結果
        // （或查無後被快取成一筆永遠命中的空白），把「這筆 LOG 沒有座標」這個事實洗掉，
        // 現場再也看不出哪些 TAG 需要補測。擋在門口才有機會叫使用者重掃。
        if (lat == 0 && lon == 0)
            return (0, 0, "lat/lon 不可同時為 0（手持機未定位時的預設值，不是有效座標）");

        return (lat, lon, null);
    }

    private static (double 值, string? 錯誤) 解析座標(string? raw, string 名, double 絕對上限)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return (0, $"缺少參數 {名}");

        // InvariantCulture：查詢字串是機器對機器的介面，小數點固定是「.」，
        // 不能跟著 Function App 所在地區的文化設定走。
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (0, $"參數 {名} 不是有效的數字：{截短(s)}");

        // NaN／Infinity 會被 TryParse 接受，而且**與任何數字的大小比較都是 false**——
        // 少了這一關，"NaN" 會直接通過下面的範圍檢查（審查最容易漏掉的一種）。
        if (!double.IsFinite(v))
            return (0, $"參數 {名} 不是有效的數字：{截短(s)}");

        if (Math.Abs(v) > 絕對上限)
            return (0, $"參數 {名} 超出範圍（|{名}| 必須 ≤ {絕對上限.ToString(CultureInfo.InvariantCulture)}）：{截短(s)}");

        return (v, null);
    }

    private static string 截短(string s) => s.Length <= 50 ? s : s[..50] + "…";

    /// <summary>
    /// 寫快取失敗不影響回應：地址已經查到了，寫不進去只是下次還要再打上游一次。
    /// （同 UsageLog 的原則——次要動作不可以讓主要結果失敗。）
    /// </summary>
    private async Task 寫快取(Microsoft.Data.SqlClient.SqlConnection cn, string 鍵, double lat, double lon,
        ReverseHit? hit, string 來源, CancellationToken ct)
    {
        try
        {
            await ReverseGeocodeCache.UpsertAsync(cn, 鍵, lat, lon, hit?.Address,
                hit?.Source ?? 來源, hit?.Precision, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫 reverse-geocode 快取失敗（不影響回應）");
        }
    }

    /// <summary>
    /// 回應（合約凍結，ERP 端已照這個形狀寫解析——欄位改名就對不上）：
    ///   找到 <c>{ found:true, lat, lon, address, source, precision, cached }</c>
    ///   查無 <c>{ found:false, lat, lon, source, cached }</c>
    /// lat/lon 是呼叫端送來的**原值**，不是快取的格網化值。
    /// </summary>
    private static Task<HttpResponseData> 回應(HttpRequestData req, double lat, double lon, bool 找到,
        string? address, string? source, string? precision, bool cached)
        => 找到
            ? ApiPipeline.Json(req, HttpStatusCode.OK,
                new { found = true, lat, lon, address, source, precision, cached })
            // 查無時不給 address/precision（沒有值就不要放欄位，免得客戶端把 null 當空字串寫進主檔）
            : ApiPipeline.Json(req, HttpStatusCode.OK,
                new { found = false, lat, lon, source, cached });

    private async Task<HttpResponseData> 錯誤(HttpRequestData req, int clientId, string? 參數,
        Stopwatch sw, HttpStatusCode code, string 訊息)
    {
        await UsageLog.WriteAsync(_logger, clientId, Dataset, 參數, null, sw.ElapsedMilliseconds, (int)code, 訊息);
        return await ApiPipeline.Json(req, code, new { error = 訊息 });
    }
}
