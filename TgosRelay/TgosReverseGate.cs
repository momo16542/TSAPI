namespace TgosRelay;

/// <summary>
/// 反查結果要不要因為距離太遠被判定「查無」。抽成純函式讓測試能覆蓋邊界值，
/// 也讓 <c>Program.cs</c> 的 <c>/reverse</c> 端點只呼叫、不重複判斷邏輯。
/// </summary>
public static class TgosReverseGate
{
    /// <summary>
    /// <paramref name="maxDistanceM"/>（<see cref="RelayConfig.GeoMaxDistanceM"/>）≤ 0 表示不限制，
    /// 一律回 false（不擋）。<paramref name="dis"/>（<see cref="TgosReverseHit.Dis"/>）沒有值時也不擋——
    /// 上游少給一個欄位不該讓整批查不到；呼叫端要自己 log 一筆記這個情況。
    /// 等於門檻（<c>dis == maxDistanceM</c>）視為通過，只有**超過**才算太遠。
    /// </summary>
    public static bool 太遠(double? dis, int maxDistanceM)
        => maxDistanceM > 0 && dis is double d && d > maxDistanceM;
}
