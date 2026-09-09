namespace TS.API.ExtData.Geocode;

/// <summary>
/// Nominatim 的 1 request/second 節流閘（2026-09-09 由 <see cref="NominatimBackend"/> 抽出共用）。
///
/// **為什麼一定要共用**：使用政策
/// （https://operations.osmfoundation.org/policies/nominatim/）的 1 req/s
/// 是「一個呼叫端對整個 nominatim.openstreetmap.org」的加總上限，**不是每個端點各一份**。
/// 正查 <c>/search</c> 與反查 <c>/reverse</c> 各自帶一個節流器的話，
/// 兩支同時忙就是 2 req/s——政策上等同違規，而且不會有任何錯誤訊息告訴你，
/// 只會某天整個來源 IP 被封（那時所有客戶的定位一起壞掉）。
/// 所以閘門與「上次送出時間」都放在這裡的 static，全行程共用一條隊伍。
///
/// ⚠️ 界線同抽出前：狀態是**本行程內**的 static，Functions 多實例時各算各的，
///    整體仍可能超過 1 req/s。現階段流量極小（拜訪行程規劃與 TAG 匯入，
///    一天數十筆且大多命中快取），可接受；換 TGOS 之後這個限制自然消失。
/// </summary>
internal static class NominatimThrottle
{
    /// <summary>兩次送出之間的最小間隔（政策硬性上限 1 req/s）。</summary>
    public static readonly TimeSpan 最小間隔 = TimeSpan.FromSeconds(1);

    private static readonly SemaphoreSlim _閘 = new(1, 1);
    private static DateTime _上次送出 = DateTime.MinValue;

    /// <summary>
    /// 排隊等到可以送出下一個請求為止。取消（呼叫端斷線）時丟 <see cref="OperationCanceledException"/>——
    /// 客戶端都走了還讓別人替沒人要的答案排隊，等於白花共用的 1 req/s 預算。
    /// </summary>
    public static async Task 等到可以送出(CancellationToken ct)
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
}
