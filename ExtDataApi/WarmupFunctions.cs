using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace TS.API.ExtData;

/// <summary>
/// 暖機端點 GET /api/v1/warmup（2026-10-02）。
///
/// 為什麼要有：YYAPI 閒置約 150 秒就回收 instance，新 worker 程序第一次走授權（v1/license）
/// 要付約 1.8 秒的一次性初始化（受控識別取權杖、SQL 首連、JIT、ECDsa 首用）；
/// 只叫醒 Function 主機（GetDateTime）對這段完全沒幫助（ERPV2 .claude/tmp/yyapi-license-coldpath.md）。
/// ERP 在登入畫面出現時先打這支，讓使用者輸入帳密的時間把那段吃掉；外部定時 ping 也打這支。
///
/// 安全邊界（匿名、不需要金鑰，所以必須是「打幾次都無害」）：
/// - 不收參數、不回任何資料：成功回固定 <c>{"ok":true}</c>，失敗回固定 503 <c>{"ok":false}</c>，不帶例外訊息。
/// - 每個 worker 程序只真正預熱一次；並行呼叫共享同一次預熱。預熱成功後再打**不碰 DB**。
/// - 預熱失敗後 <see cref="失敗冷卻"/> 內不重試（直接回 503），避免被拿來放大打中央庫。
/// - 不寫用量表（api_usage 只記真正的 API 呼叫）。
/// </summary>
public class WarmupFunctions
{
    /// <summary>預熱失敗後多久內不再重試。</summary>
    internal static readonly TimeSpan 失敗冷卻 = TimeSpan.FromSeconds(30);

    private static readonly object 鎖 = new();
    private static Task<bool>? _預熱中;
    private static DateTime _上次失敗Utc = DateTime.MinValue;

    // 分段完成旗標：重試時只補做失敗的那段，已完成的（尤其 DB）不再碰。
    private static volatile bool _資料庫已預熱;
    private static volatile bool _簽章已預熱;

    private readonly ILogger _logger;

    public WarmupFunctions(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<WarmupFunctions>();

    [Function("Warmup")]
    public async Task<HttpResponseData> Warmup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/warmup")] HttpRequestData req,
        CancellationToken ct)
    {
        bool ok;
        try
        {
            // WaitAsync：呼叫端斷線只讓「這個請求」不等了，共享的預熱照跑完。
            ok = await 確保預熱(_logger).WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            ok = false;
        }

        var res = req.CreateResponse(ok ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        res.Headers.Add("Cache-Control", "no-store");
        await res.WriteStringAsync(ok ? "{\"ok\":true}" : "{\"ok\":false}");
        return res;
    }

    /// <summary>
    /// 取得（必要時啟動）本程序的預熱工作。
    /// 已成功 → 回已完成的 true；進行中 → 回同一個 Task；失敗且在冷卻期內 → 回 false 不重試。
    /// </summary>
    internal static Task<bool> 確保預熱(ILogger logger)
    {
        lock (鎖)
        {
            if (_預熱中 is { } 現有)
            {
                if (!現有.IsCompleted) return 現有;                        // 進行中：共享
                if (現有.IsCompletedSuccessfully && 現有.Result) return 現有; // 已成功：不再碰 DB
                if (DateTime.UtcNow - _上次失敗Utc < 失敗冷卻) return 現有;   // 失敗冷卻中
            }

            // Task.Run：預熱不吃第一個呼叫者的同步內容，也不讓它的取消影響別人。
            _預熱中 = Task.Run(() => 預熱Async(logger));
            return _預熱中;
        }
    }

    private static async Task<bool> 預熱Async(ILogger logger)
    {
        var 總 = Stopwatch.StartNew();
        long 權杖ms = -1, sql首連ms = -1, 簽章ms = -1;

        if (!_資料庫已預熱)
        {
            try
            {
                var 段 = Stopwatch.StartNew();
                await CentralDb.GetTokenAsync();
                權杖ms = 段.ElapsedMilliseconds;

                段.Restart();
                // OpenAsync 內會再取一次權杖——此時已在 credential 快取裡，不會再打身分端點。
                await using (var cn = await CentralDb.OpenAsync())
                await using (var cmd = new SqlCommand("SELECT 1", cn))
                {
                    await cmd.ExecuteScalarAsync();
                }
                sql首連ms = 段.ElapsedMilliseconds;
                _資料庫已預熱 = true;
            }
            catch (Exception ex)
            {
                // 例外細節只進 log，不進回應。
                logger.LogWarning(ex, "暖機：中央庫預熱失敗（權杖={權杖}ms）", 權杖ms);
            }
        }

        if (!_簽章已預熱)
        {
            try
            {
                var 段 = Stopwatch.StartNew();
                if (LicenseSigner.預熱())
                {
                    簽章ms = 段.ElapsedMilliseconds;
                    _簽章已預熱 = true;
                }
                else
                {
                    logger.LogWarning("暖機：app setting {設定} 未設定，略過簽章預熱", LicenseSigner.私鑰環境變數);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "暖機：簽章預熱失敗");
            }
        }

        var 成功 = _資料庫已預熱 && _簽章已預熱;
        if (!成功)
        {
            lock (鎖) _上次失敗Utc = DateTime.UtcNow;
        }

        logger.LogInformation(
            "暖機 {結果} 取權杖={權杖}ms SQL首連={SQL}ms 簽章={簽章}ms 總={總}ms（-1＝本輪未做或失敗）",
            成功 ? "完成" : "未完成", 權杖ms, sql首連ms, 簽章ms, 總.ElapsedMilliseconds);
        return 成功;
    }
}
