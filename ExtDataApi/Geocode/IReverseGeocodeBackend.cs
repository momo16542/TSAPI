namespace TS.API.ExtData.Geocode;

/// <summary>
/// 一次反查（座標 → 地址）的結果。查無由 <see cref="IReverseGeocodeBackend.ReverseAsync"/>
/// 回 null 表示，不用空字串當哨兵值——「查到但地址是空的」與「查不到」是兩件事，
/// 混在一起的話呼叫端會把空白地址寫進主檔（同 <see cref="GeocodeHit"/> 不用 (0,0) 的理由）。
/// </summary>
/// <param name="Address">反查到的地址（顯示用字串，未經 <see cref="AddressKey"/> 正規化）。</param>
/// <param name="Source">來源：NOMINATIM／（將來）TGOS。</param>
/// <param name="Precision">
/// 精度，值域與正查完全相同（合約凍結）：<c>house</c>／<c>road-fallback</c>。
/// 判定的唯一實作一樣是 <see cref="GeoPrecision"/>，依據是**上游回應物件的層級**，
/// 不是「反查回來的地址字串裡有沒有『號』」——理由見該類別。
/// </param>
public sealed record ReverseHit(string Address, string Source, string Precision);

/// <summary>
/// 反查後端（座標 → 地址）。與正查 <see cref="IGeocodeBackend"/> 刻意分成兩個介面而不是加方法：
/// 兩者的上游端點、參數、快取表、資料集授權都不同，而且**換上游的時程不一樣**——
/// 正查已經可以換 TGOS，反查要等轉發器的 <c>/reverse</c> 上線（見 <see cref="ReverseGeocodeBackendFactory"/>）。
/// 綁在同一個介面上，就會逼出「實作了一半」的後端。
/// </summary>
public interface IReverseGeocodeBackend
{
    /// <summary>來源代碼（寫進快取與回應的 <c>source</c>）。</summary>
    string 來源 { get; }

    /// <summary>
    /// 反查一組座標。查無資料回 null；上游故障丟 <see cref="GeocodeBackendException"/>
    /// （**沿用正查那個例外型別**：HTTP 殼對「故障 vs 查無」的處理兩邊一模一樣，
    /// 各開一個例外只會讓 catch 區塊變兩倍長而沒有任何新資訊）。
    /// </summary>
    /// <param name="lat">緯度（WGS84）。</param>
    /// <param name="lon">經度（WGS84）。</param>
    Task<ReverseHit?> ReverseAsync(double lat, double lon, CancellationToken ct = default);
}

/// <summary>依 app setting <c>REVERSE_BACKEND</c> 選反查後端（預設 NOMINATIM）。</summary>
public static class ReverseGeocodeBackendFactory
{
    public const string 設定鍵 = "REVERSE_BACKEND";

    /// <summary>
    /// 後端無狀態（節流狀態在 <see cref="NominatimThrottle"/> 的 static），建一次共用。
    ///
    /// <c>PublicationOnly</c> 的理由與正查工廠完全相同（2026-09-03 審查 S4）：
    /// 預設的 <c>ExecutionAndPublication</c> **會把例外一起快取**，於是 <c>REVERSE_BACKEND</c>
    /// 設錯（或現階段設成 TGOS）之後，即使把 app setting 改對，這個 Lazy 仍會永遠重丟同一個例外，
    /// 非得重啟 Function App 不可——「改了設定卻沒生效、還查不出原因」的典型。
    /// PublicationOnly 不快取例外，下一次請求就重新讀設定；代價是併發時可能多建幾個後端物件，
    /// 而後端無狀態，多建幾個沒有副作用。
    /// </summary>
    private static readonly Lazy<IReverseGeocodeBackend> _預設 = new(() => 建立(
        Environment.GetEnvironmentVariable(設定鍵)), LazyThreadSafetyMode.PublicationOnly);

    public static IReverseGeocodeBackend 預設後端 => _預設.Value;

    public static IReverseGeocodeBackend 建立(string? 名稱)
    {
        var n = string.IsNullOrWhiteSpace(名稱) ? "NOMINATIM" : 名稱!.Trim().ToUpperInvariant();
        return n switch
        {
            "NOMINATIM" => new NominatimReverseBackend(),
            // TGOS 反查要等 GCP 轉發器開出 /reverse 端點（正查用的是 /geocode）。
            // 這裡刻意丟例外而不是默默退回 Nominatim：設定寫了 TGOS 卻跑 Nominatim，
            // 會讓人以為門牌精度已經生效（同正查工廠對 TGOS 缺設定的處置）。
            "TGOS" => throw new NotSupportedException(
                $"{設定鍵}=TGOS 尚未支援：TGOS 反查待轉發器 /reverse 上線（目前轉發器只有 /geocode 正查）"),
            _ => throw new NotSupportedException($"未知的 {設定鍵}：{名稱}"),
        };
    }
}
