using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace TS.API.ExtData;

/// <summary>
/// 對外 API 的共用前置流程（2026-09-03 由 <see cref="ExtDataFunctions"/> 抽出）。
///
/// 抽出的理由：認證／授權／用量三件事必須**每支端點都一模一樣**——
/// 少寫一段用量就等於那支端點的「哪家客戶幾天沒來拉」看不出來，
/// 少判一次資料集就等於未開通的客戶拿得到資料。複製貼上遲早會漏，所以集中一處。
///
/// 行為與抽出前完全相同（401 不細分原因、403 附已開通清單、兩者都先寫用量再回應）。
/// </summary>
internal static class ApiPipeline
{
    /// <summary>
    /// 驗金鑰＋資料集授權。
    /// 通過 → (呼叫端, null)；不通過 → (null, 已寫好的 401/403 回應，直接回傳給呼叫端即可)。
    /// 兩種不通過都會先寫用量紀錄（金鑰無效時 client_id 記 NULL，那才看得出有人在試金鑰）。
    /// </summary>
    public static async Task<(ApiCaller? 呼叫端, HttpResponseData? 拒絕回應)> 驗證Async(
        ILogger logger, HttpRequestData req, string dataset, string? 參數, Stopwatch sw)
    {
        req.Headers.TryGetValues(ApiAuth.HeaderName, out var keys);
        var caller = await ApiAuth.VerifyAsync(keys?.FirstOrDefault(), CancellationToken.None);

        if (caller is null)
        {
            await UsageLog.WriteAsync(logger, null, dataset, 參數, null, sw.ElapsedMilliseconds, 401, "金鑰無效");
            // 不細分「金鑰不存在／已撤銷／客戶停用」——避免用回應內容幫人試金鑰
            return (null, await Json(req, HttpStatusCode.Unauthorized, new { error = "金鑰無效或已停用" }));
        }

        if (!caller.CanRead(dataset))
        {
            await UsageLog.WriteAsync(logger, caller.ClientId, dataset, 參數, null, sw.ElapsedMilliseconds, 403, "未開通此資料集");
            return (null, await Json(req, HttpStatusCode.Forbidden,
                new { error = $"未開通資料集 {dataset}", 已開通 = caller.Datasets }));
        }

        return (caller, null);
    }

    /// <summary>統一的 JSON 回應（UTF-8、中文不轉義）。</summary>
    public static async Task<HttpResponseData> Json(HttpRequestData req, HttpStatusCode code, object body)
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
}
