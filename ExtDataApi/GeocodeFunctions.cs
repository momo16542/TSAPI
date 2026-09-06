using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TS.API.ExtData.Geocode;

namespace TS.API.ExtData;

/// <summary>
/// 地址定位 API（2026-09-03）。各客戶 ERP 的拜訪行程規劃經此把地址換成座標。
///
/// 為什麼要集中在中央而不是各客戶自己打上游：
///   ① TGOS（之後要換的門牌權威來源）採 **IP 驗證**，各客戶對外 IP 不同又會換——
///      集中一個呼叫端只要登記一個 IP，金鑰也只放中央的 Key Vault，不必下放到 N 個客戶。
///   ② Nominatim 的 1 req/s 是所有人加總的上限，集中才管得住。
///   ③ 地址高度重疊，中央快取一次命中所有客戶都受惠。
///
/// 認證／授權／用量全部沿用既有那一套（<see cref="ApiPipeline"/>）：資料集名 <c>geocode</c>，
/// 只是 extdata.api_client_dataset 多一列，usp_api_key_verify 不用改。
///
/// **查無地址回 200 found=false，不是 404**：查不到是正常結果。
/// 用 4xx 表達會讓不重試的客戶端誤判成「服務壞了」，也讓用量統計分不出故障與查無。
/// 真的故障（上游連不上／回錯）才 502。
///
/// 回應的 <c>precision</c> 只有 <c>house</c>／<c>road-fallback</c> 兩個值，
/// **house＝上游回的物件本身是門牌／建物層級**（不是「送去的地址字串裡有門牌號」）——
/// 判定的唯一實作在 <see cref="Geocode.GeoPrecision"/>，呼叫端可以照字面信任 house。
///
/// <c>address</c> 的長度上限量的是**正規化後**（去空白、全形轉半形）的鍵長，
/// 與快取鍵同一把尺，見 <see cref="AddressKey.超過長度上限"/>。
/// </summary>
public class GeocodeFunctions
{
    private const string Dataset = "geocode";

    private readonly ILogger _logger;

    public GeocodeFunctions(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<GeocodeFunctions>();

    /// <param name="ct">
    /// 呼叫端斷線時由 host 取消。一路傳到查快取／打上游／寫快取三處——
    /// 客戶端都走了還讓 Nominatim 的 1 req/s 預算被佔著，等於替沒人要的答案排隊。
    /// </param>
    [Function("Geocode")]
    public async Task<HttpResponseData> Geocode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/geocode")] HttpRequestData req,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var q = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var 參數 = req.Url.Query;

        var (caller, 拒絕) = await ApiPipeline.驗證Async(_logger, req, Dataset, 參數, sw);
        if (拒絕 is not null) return 拒絕;

        var 地址 = (q["address"] ?? string.Empty).Trim();
        if (地址.Length == 0)
            return await 錯誤(req, caller!.ClientId, 參數, sw, HttpStatusCode.BadRequest, "缺少參數 address");
        // 量的是正規化後的鍵長（去空白、全形轉半形），與快取鍵同一把尺——
        // 用原字串長度擋會把「排版空白很多、實際 198 字」的地址誤判成超長（審查 S9）。
        if (AddressKey.超過長度上限(地址))
            return await 錯誤(req, caller!.ClientId, 參數, sw, HttpStatusCode.BadRequest,
                $"address 正規化後超過 {AddressKey.長度上限} 字");

        var 鍵 = AddressKey.正規化(地址);

        try
        {
            await using var cn = await CentralDb.OpenAsync();

            // 先取後端：快取命中與否要看「這筆是不是目前這個後端寫的」（換 TGOS 後舊列必須重查）。
            var backend = GeocodeBackendFactory.預設後端;

            var 快取 = await GeocodeCache.TryGetAsync(cn, 鍵, backend.來源, ct);
            // TGOS 結果的保留時數設 0＝完全不快取（GeocodeCache.找到保留時數），這時連寫都不寫
            if (快取 is not null)
            {
                await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數,
                    快取.找到 ? 1 : 0, sw.ElapsedMilliseconds, 200, "cache");
                return await 回應(req, 地址, 快取.找到, 快取.Lon, 快取.Lat,
                    快取.Source, 快取.Precision, cached: true);
            }

            var hit = await backend.GeocodeAsync(地址, ct);

            // 查無也寫快取（找到=0）——否則一個查不到的地址每次都會打上游，
            // 而上游的 1 req/s 是所有客戶共用的。過期規則見 GeocodeCache.查無保留天數。
            if (GeocodeCache.可寫快取(backend.來源))
                await 寫快取(cn, 鍵, 地址, hit, backend.來源, ct);

            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數,
                hit is null ? 0 : 1, sw.ElapsedMilliseconds, 200, "backend:" + backend.來源);
            return await 回應(req, 地址, hit is not null, hit?.Lon, hit?.Lat,
                hit?.Source ?? backend.來源, hit?.Precision, cached: false);
        }
        catch (GeocodeBackendException ex)
        {
            _logger.LogError(ex, "定位後端故障");
            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 502,
                ex.GetType().Name + " " + ex.Message);
            // 上游的錯誤內容不轉給呼叫端（含 URL 與金鑰的機會不小）
            return await ApiPipeline.Json(req, HttpStatusCode.BadGateway,
                new { error = "定位服務暫時無法使用" });
        }
        catch (Exception ex) when (ex is NotSupportedException or GeocodeConfigurationException)
        {
            // GEOCODE_BACKEND 設定錯誤（未知的值），或 TGOS 後端的設定缺漏／轉發器回 401／503（GeocodeConfigurationException）。
            // 這是**設定問題，不會自己好**，而 ERP 端把 5xx 一律當「暫時性、稍後再試」
            // （CentralGeocodeProvider.轉譯例外），所以回應本文一定要指名是哪個設定錯了，
            // 否則維運者只能去 App Insights 撈 log 才知道要改什麼（審查 S4）。
            _logger.LogError(ex, "GEOCODE_BACKEND 設定錯誤");
            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                "設定錯誤 " + GeocodeBackendFactory.設定鍵 + "：" + ex.Message);
            return await ApiPipeline.Json(req, HttpStatusCode.InternalServerError, new
            {
                error = $"伺服器設定錯誤：app setting {GeocodeBackendFactory.設定鍵} 的值無法建立定位後端"
                        + $"（{ex.Message}）。這不是暫時性故障，重試不會好，請通知提供方修正設定。",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "geocode 失敗");
            await UsageLog.WriteAsync(_logger, caller!.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                ex.GetType().Name + " " + ex.Message);
            return await ApiPipeline.Json(req, HttpStatusCode.InternalServerError,
                new { error = "查詢失敗，請聯絡提供方" });
        }
    }

    /// <summary>
    /// 寫快取失敗不影響回應：座標已經查到了，寫不進去只是下次還要再打上游一次。
    /// （同 UsageLog 的原則——次要動作不可以讓主要結果失敗。）
    /// </summary>
    private async Task 寫快取(Microsoft.Data.SqlClient.SqlConnection cn, string 鍵, string 地址,
        GeocodeHit? hit, string 來源, CancellationToken ct)
    {
        try
        {
            await GeocodeCache.UpsertAsync(cn, 鍵, 地址, hit?.Lon, hit?.Lat,
                hit?.Source ?? 來源, hit?.Precision, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫 geocode 快取失敗（不影響回應）");
        }
    }

    private static Task<HttpResponseData> 回應(HttpRequestData req, string 地址, bool 找到,
        double? lon, double? lat, string? source, string? precision, bool cached)
        => 找到
            ? ApiPipeline.Json(req, HttpStatusCode.OK,
                new { found = true, address = 地址, lon, lat, source, precision, cached })
            // 查無時不給 lon/lat/precision（沒有值就不要放欄位，免得客戶端把 null 當 0 用）
            : ApiPipeline.Json(req, HttpStatusCode.OK,
                new { found = false, address = 地址, source, cached });

    private async Task<HttpResponseData> 錯誤(HttpRequestData req, int clientId, string? 參數,
        Stopwatch sw, HttpStatusCode code, string 訊息)
    {
        await UsageLog.WriteAsync(_logger, clientId, Dataset, 參數, null, sw.ElapsedMilliseconds, (int)code, 訊息);
        return await ApiPipeline.Json(req, code, new { error = 訊息 });
    }
}
