using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace TS.API.ExtData;

/// <summary>
/// 外部資料查詢 API（2026-09-01）。各客戶 ERP 經此取得公會統計與金屬行情，
/// 不再各自抓取、也不直連中央庫（1433 常被企業防火牆擋，且不該讓 N 家客戶直連正式庫）。
///
/// 認證＝每客戶一把金鑰（<c>X-Api-Key</c>），授權＝依資料集開通；
/// 兩者都由 <see cref="ApiAuth"/> 交給 DB 的 usp_api_key_verify 判定，規則集中一處。
///
/// **本類刻意只留 HTTP 殼**：驗金鑰在 ApiAuth、查詢在 ExtDataQueries、用量在 UsageLog，
/// 都是可以被治具直接呼叫的真元件——沒有 Functions 執行環境時仍驗得動實質邏輯。
/// </summary>
public class ExtDataFunctions
{
    private readonly ILogger _logger;

    public ExtDataFunctions(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<ExtDataFunctions>();

    [Function("ExtDataTtvma")]
    public Task<HttpResponseData> Ttvma(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/ttvma")] HttpRequestData req)
        => Handle(req, "ttvma", (cn, q, limit, cursor) =>
            ExtDataQueries.Ttvma(cn, ParseMonth(q["since"]), Blank(q["vehicle"]), limit, cursor));

    [Function("ExtDataMetalPrice")]
    public Task<HttpResponseData> MetalPrice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/metalprice")] HttpRequestData req)
        => Handle(req, "metalprice", (cn, q, limit, cursor) =>
            ExtDataQueries.MetalPrice(cn, ParseDate(q["since"]), Blank(q["item"]), limit, cursor));

    [Function("ExtDataMetalItem")]
    public Task<HttpResponseData> MetalItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/metalitem")] HttpRequestData req)
        => Handle(req, "metalitem", (cn, _, limit, cursor) =>
            ExtDataQueries.MetalItem(cn, limit, cursor));

    // ────────────────────────── 共用流程 ──────────────────────────

    private async Task<HttpResponseData> Handle(HttpRequestData req, string dataset,
        Func<SqlConnection, System.Collections.Specialized.NameValueCollection, int, long, Task<PagedRows>> query)
    {
        var sw = Stopwatch.StartNew();
        var q = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var 參數 = req.Url.Query;

        req.Headers.TryGetValues(ApiAuth.HeaderName, out var keys);
        var caller = await ApiAuth.VerifyAsync(keys?.FirstOrDefault(), CancellationToken.None);

        if (caller is null)
        {
            await UsageLog.WriteAsync(_logger, null, dataset, 參數, null, sw.ElapsedMilliseconds, 401, "金鑰無效");
            // 不細分「金鑰不存在／已撤銷／客戶停用」——避免用回應內容幫人試金鑰
            return await Json(req, HttpStatusCode.Unauthorized, new { error = "金鑰無效或已停用" });
        }
        if (!caller.CanRead(dataset))
        {
            await UsageLog.WriteAsync(_logger, caller.ClientId, dataset, 參數, null, sw.ElapsedMilliseconds, 403, "未開通此資料集");
            return await Json(req, HttpStatusCode.Forbidden,
                new { error = $"未開通資料集 {dataset}", 已開通 = caller.Datasets });
        }

        int limit = Math.Clamp(ParseInt(q["limit"]) ?? ExtDataQueries.DefaultLimit, 1, ExtDataQueries.MaxLimit);
        long cursor = ParseLong(q["cursor"]) ?? 0;

        try
        {
            await using var cn = await CentralDb.OpenAsync();
            var page = await query(cn, q, limit, cursor);

            await UsageLog.WriteAsync(_logger, caller.ClientId, dataset, 參數, page.Count, sw.ElapsedMilliseconds, 200);
            return await Json(req, HttpStatusCode.OK, new
            {
                data = page.Data,
                nextCursor = page.NextCursor,
                count = page.Count,
                serverTime = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Dataset} 查詢失敗", dataset);
            await UsageLog.WriteAsync(_logger, caller.ClientId, dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                ex.GetType().Name + " " + ex.Message);
            // 例外內容不回給呼叫端（可能含連線字串或結構資訊）
            return await Json(req, HttpStatusCode.InternalServerError, new { error = "查詢失敗，請聯絡提供方" });
        }
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, HttpStatusCode code, object body)
    {
        var res = req.CreateResponse(code);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await res.WriteStringAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            // 中文不要被轉成 \uXXXX，方便人直接讀回應
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return res;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static int? ParseInt(string? s) => int.TryParse(s, out var v) ? v : null;
    private static long? ParseLong(string? s) => long.TryParse(s, out var v) ? v : null;
    private static DateTime? ParseDate(string? s) => DateTime.TryParse(s, out var d) ? d.Date : null;
    private static DateTime? ParseMonth(string? s)
        => DateTime.TryParse(s, out var d) ? new DateTime(d.Year, d.Month, 1) : null;
}
