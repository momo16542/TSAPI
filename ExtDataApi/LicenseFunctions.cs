using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace TS.API.ExtData;

/// <summary>
/// 中央授權心跳（2026-09-05）。契約：ERPV2 repo 的 .claude/tmp/license-contract.md，
/// **兩側實作都以該檔為準**，這裡不自行變體。
///
/// 用途：TsERP 客戶端每次登入（與每輪排程）打一次，取得該客戶的授權狀態與到期日；
/// 逾期且超過寬限期就擋登入。判定規則全在客戶端，本端只負責「講事實並簽名」。
///
/// **不做 api_client_dataset 資料集檢查**（與 <see cref="ApiPipeline.驗證Async"/> 的差別）：
/// 授權查詢對所有有效金鑰開放。理由是資料集開通本身是客服動作，
/// 若忘了開 license 這一列，客戶會在客戶端被擋在登入畫面外——
/// 把「沒開通資料集」變成「不能開程式」是最糟的失敗模式。
/// 用量 (api_usage) 照寫，dataset 記 'license'，才看得出哪家幾天沒來心跳。
///
/// 金鑰無效回 401；缺簽章私鑰或其他錯誤回 500——客戶端把 5xx／連不上一律視為
/// 「無法連線」走本機快取，所以 500 不會直接把客戶擋在門外，但也絕不能無簽章放行
/// （沒簽章的回應等同任何人都能偽造授權）。
/// </summary>
public class LicenseFunctions
{
    private const string Dataset = "license";

    private readonly ILogger _logger;

    public LicenseFunctions(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<LicenseFunctions>();

    [Function("License")]
    public async Task<HttpResponseData> License(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/license")] HttpRequestData req,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var 參數 = req.Url.Query;

        ApiCaller? caller;
        try
        {
            req.Headers.TryGetValues(ApiAuth.HeaderName, out var keys);
            caller = await ApiAuth.VerifyAsync(keys?.FirstOrDefault(), ct);
        }
        catch (Exception ex)
        {
            // 驗金鑰要連中央庫，所以這一步**也會**因為連不上／取不到權杖而丟例外。
            // 不接的話例外直接冒出 Function：host 回一個沒有本文的 500、
            // api_usage 一列都不會有、自家 log 也沒有——
            // 就成了「全體客戶端被擋在門外，而中央完全看不出發生過什麼」。
            _logger.LogError(ex, "驗金鑰失敗（多半是中央庫連不上）");
            await UsageLog.WriteAsync(_logger, null, Dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                "驗金鑰失敗 " + ex.GetType().Name + " " + ex.Message);
            return await ApiPipeline.Json(req, HttpStatusCode.InternalServerError,
                new { error = "授權查詢失敗，請聯絡提供方" });
        }

        if (caller is null)
        {
            // 不細分「金鑰不存在／已撤銷／客戶停用」——避免用回應內容幫人試金鑰（同 ApiPipeline）。
            // client_id 記 NULL 但仍要寫，那才看得出設定錯誤或有人在試金鑰。
            await UsageLog.WriteAsync(_logger, null, Dataset, 參數, null, sw.ElapsedMilliseconds, 401, "金鑰無效");
            return await ApiPipeline.Json(req, HttpStatusCode.Unauthorized, new { error = "金鑰無效或已停用" });
        }

        // 先確認簽得了再查庫：私鑰沒設定時查了也沒用，而且這是設定問題不會自己好。
        var 私鑰 = LicenseSigner.讀私鑰Base64();
        if (私鑰 is null)
        {
            _logger.LogError("app setting {設定} 未設定，無法簽發授權回應", LicenseSigner.私鑰環境變數);
            await UsageLog.WriteAsync(_logger, caller.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                "缺 " + LicenseSigner.私鑰環境變數);
            return await ApiPipeline.Json(req, HttpStatusCode.InternalServerError,
                new { error = "伺服器未設定簽章金鑰" });
        }

        try
        {
            var 授權 = await 查授權(caller, ct);

            var 到期日 = 授權.到期日?.ToString(LicenseSigner.到期日格式, CultureInfo.InvariantCulture);
            var 簽發時間 = DateTime.UtcNow.ToString(LicenseSigner.簽發時間格式, CultureInfo.InvariantCulture);
            var 簽章 = LicenseSigner.簽章(私鑰,
                LicenseSigner.組被簽字串(授權.代號, 授權.狀態, 到期日, 授權.寬限天數, 簽發時間));

            await UsageLog.WriteAsync(_logger, caller.ClientId, Dataset, 參數, 1, sw.ElapsedMilliseconds, 200,
                授權.狀態);

            // 欄名逐字照契約（含中文欄名與順序）；狀態 none 時到期日為 null。
            return await ApiPipeline.Json(req, HttpStatusCode.OK, new
            {
                client = 授權.代號,
                名稱 = 授權.名稱,
                狀態 = 授權.狀態,
                到期日,
                寬限天數 = 授權.寬限天數,
                簽發時間,
                簽章,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "授權查詢失敗");
            await UsageLog.WriteAsync(_logger, caller.ClientId, Dataset, 參數, null, sw.ElapsedMilliseconds, 500,
                ex.GetType().Name + " " + ex.Message);
            return await ApiPipeline.Json(req, HttpStatusCode.InternalServerError,
                new { error = "授權查詢失敗，請聯絡提供方" });
        }
    }

    private sealed record 授權狀態(string 代號, string 名稱, string 狀態, DateTime? 到期日, int 寬限天數);

    /// <summary>
    /// 查 extdata.usp_license_get。判定規則（無列＝none、預設寬限 60）集中在該 proc，
    /// 這裡只做「proc 連客戶列都查不到」的最後防線——正常不會發生（金鑰剛驗過），
    /// 但真發生時回 none 比丟例外好：客戶端看到 none 會顯示「尚未設定授權」，
    /// 丟例外則變成 500、客戶端改吃舊快取，反而看不出中央資料有問題。
    /// </summary>
    private static async Task<授權狀態> 查授權(ApiCaller caller, CancellationToken ct)
    {
        await using var cn = await CentralDb.OpenAsync(ct);
        await using var cmd = new SqlCommand("extdata.usp_license_get", cn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
        };
        cmd.Parameters.AddWithValue("@client_id", caller.ClientId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return new 授權狀態(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.GetInt32(4));
        }

        return new 授權狀態(caller.代號, caller.名稱, "none", null, 60);
    }
}
