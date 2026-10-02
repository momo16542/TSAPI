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
/// - 不收參數、不回任何資料：成功回固定 <c>{"ok":true}</c>，失敗（含任何例外）回固定 503 <c>{"ok":false}</c>。
/// - 每個 worker 程序只真正預熱一次；並行呼叫共享同一次預熱。預熱成功後再打**不碰 DB**。
/// - 預熱失敗後 <see cref="失敗冷卻"/> 內不重試，避免被拿來放大打中央庫；重試只補失敗的段。
/// - 不寫用量表（api_usage 只記真正的 API 呼叫）。
/// - 不是健康檢查：成功一次之後永遠回 200，不代表中央庫此刻可用。
///
/// 函式名刻意**不叫 Warmup**：Flex／Premium 平台 scale-out 時會找名為 warmup 的函式當 warmup trigger
/// （learn.microsoft.com/azure/azure-functions/functions-bindings-warmup），撞名的行為文件沒寫。
/// </summary>
public class WarmupFunctions
{
    /// <summary>預熱失敗後多久內不再重試。</summary>
    internal static readonly TimeSpan 失敗冷卻 = TimeSpan.FromSeconds(30);

    /// <summary>本程序唯一的預熱閘（三段：取權杖 → SQL 首連 → 簽章）。</summary>
    private static readonly 預熱閘 _閘 = new(
        new (string, Func<Task>)[]
        {
            ("取權杖", async () => await CentralDb.GetTokenAsync()),
            ("SQL首連", async () =>
            {
                // OpenAsync 內會再取一次權杖——此時已在 credential 快取裡，不會再打身分端點。
                await using var cn = await CentralDb.OpenAsync();
                await using var cmd = new SqlCommand("SELECT 1", cn);
                await cmd.ExecuteScalarAsync();
            }),
            ("簽章", () =>
            {
                if (!LicenseSigner.預熱())
                    throw new InvalidOperationException($"app setting {LicenseSigner.私鑰環境變數} 未設定");
                return Task.CompletedTask;
            }),
        },
        失敗冷卻);

    private readonly ILogger _logger;

    public WarmupFunctions(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<WarmupFunctions>();

    [Function("LicenseWarmup")]
    public async Task<HttpResponseData> Warmup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/warmup")] HttpRequestData req,
        CancellationToken ct)
    {
        bool ok;
        try
        {
            // WaitAsync：呼叫端斷線只讓「這個請求」不等了，共享的預熱照跑完。
            ok = await _閘.確保預熱(_logger).WaitAsync(ct);
        }
        catch (Exception)
        {
            // 任何例外（含取消）一律固定 503，不帶內容；細節已在預熱閘內記 log。
            ok = false;
        }

        var res = req.CreateResponse(ok ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        res.Headers.Add("Cache-Control", "no-store");
        await res.WriteStringAsync(ok ? "{\"ok\":true}" : "{\"ok\":false}");
        return res;
    }
}

/// <summary>
/// 「每程序只做一次、並行共享、失敗冷卻、只重試失敗段」的預熱閘。
/// 抽成獨立類別是為了讓測試能注入假的段與時鐘（TgosRelay.Tests/WarmupGateTests.cs）。
/// 回傳的 Task **永不 faulted**：任何例外都視同失敗（回 false 並記失敗時間，冷卻照樣生效）。
/// </summary>
internal sealed class 預熱閘
{
    private readonly (string 名稱, Func<Task> 動作)[] _段;
    private readonly bool[] _已完成;
    private readonly TimeSpan _冷卻;
    private readonly Func<DateTime> _現在;

    private readonly object _鎖 = new();
    private Task<bool>? _預熱中;
    private DateTime _上次失敗Utc = DateTime.MinValue;

    public 預熱閘((string 名稱, Func<Task> 動作)[] 段, TimeSpan 冷卻, Func<DateTime>? 現在 = null)
    {
        _段 = 段;
        _已完成 = new bool[段.Length];
        _冷卻 = 冷卻;
        _現在 = 現在 ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// 取得（必要時啟動）預熱工作。
    /// 已成功 → 回已完成的 true；進行中 → 回同一個 Task；失敗且在冷卻期內 → 回 false 不重試。
    /// </summary>
    public Task<bool> 確保預熱(ILogger logger)
    {
        lock (_鎖)
        {
            if (_預熱中 is { } 現有)
            {
                if (!現有.IsCompleted) return 現有;                                 // 進行中：共享
                if (現有.IsCompletedSuccessfully && 現有.Result) return 現有;          // 已成功：不再碰 DB
                if (_現在() - _上次失敗Utc < _冷卻) return Task.FromResult(false);    // 失敗（含 faulted）冷卻中
            }

            // Task.Run：預熱不吃第一個呼叫者的同步內容，也不讓它的取消影響別人。
            _預熱中 = Task.Run(() => 執行Async(logger));
            return _預熱中;
        }
    }

    private async Task<bool> 執行Async(ILogger logger)
    {
        var 成功 = false;
        try
        {
            成功 = await 各段Async(logger);
            return 成功;
        }
        catch
        {
            // 這裡不再 log（丟例外的可能正是 logger 本身）；冷卻在 finally 照樣生效。
            成功 = false;
            return false;
        }
        finally
        {
            if (!成功)
            {
                lock (_鎖) _上次失敗Utc = _現在();
            }
        }
    }

    private async Task<bool> 各段Async(ILogger logger)
    {
        var 總 = Stopwatch.StartNew();
        var 耗時 = new long[_段.Length];
        Array.Fill(耗時, -1L);

        for (var i = 0; i < _段.Length; i++)
        {
            if (_已完成[i]) continue;   // 已完成的段（尤其 DB）重試時不再碰
            var 段 = Stopwatch.StartNew();
            try
            {
                await _段[i].動作();
                耗時[i] = 段.ElapsedMilliseconds;
                _已完成[i] = true;
            }
            catch (Exception ex)
            {
                // 例外細節只進 log，不進回應。
                logger.LogWarning(ex, "暖機：{段} 失敗（{耗時}ms）", _段[i].名稱, 段.ElapsedMilliseconds);
            }
        }

        var 成功 = _已完成.All(x => x);
        logger.LogInformation("暖機 {結果} {分段} 總={總}ms（-1＝本輪未做或失敗）",
            成功 ? "完成" : "未完成",
            string.Join(" ", _段.Select((s, i) => $"{s.名稱}={耗時[i]}ms")),
            總.ElapsedMilliseconds);
        return 成功;
    }
}
