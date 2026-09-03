namespace TS.API.ExtData.Geocode;

/// <summary>
/// 一次定位的結果。查無資料由 <see cref="IGeocodeBackend.GeocodeAsync"/> 回 null 表示，
/// 不用「經緯度 0」當哨兵值——(0,0) 是幾內亞灣上的一個真實座標，
/// 混進來只會變成地圖上一個沒人看得懂的點。
/// </summary>
/// <param name="Lon">經度（WGS84）。</param>
/// <param name="Lat">緯度（WGS84）。</param>
/// <param name="Source">來源：NOMINATIM／（將來）TGOS。</param>
/// <param name="Precision">
/// 精度，只有兩個值（合約凍結）：<c>house</c>＝門牌／建物層級命中；
/// <c>road-fallback</c>＝其餘一切（退到路名層級才查到的，或回應物件根本不是門牌）。
/// **house 的判定依據是上游回應「物件的層級」（<c>addresstype</c>，退 <c>type</c>／<c>category</c>），
/// 不是「輸入字串裡有門牌號」**——判定的唯一實作在 <see cref="GeoPrecision"/>，
/// 新後端一律走它，否則 house 就只是宣稱、呼叫端沒有理由相信它（2026-09-03 審查 S1）。
/// </param>
public sealed record GeocodeHit(double Lon, double Lat, string Source, string Precision);

/// <summary>定位後端。換上游（Nominatim → TGOS）只換這層實作，HTTP 殼與快取都不動。</summary>
public interface IGeocodeBackend
{
    /// <summary>來源代碼（寫進快取與回應的 <c>source</c>）。</summary>
    string 來源 { get; }

    /// <summary>查一個地址。查無資料回 null；上游故障丟 <see cref="GeocodeBackendException"/>。</summary>
    Task<GeocodeHit?> GeocodeAsync(string 地址, CancellationToken ct = default);
}

/// <summary>
/// 上游定位服務故障（連不上、逾時、回 5xx／4xx）。
/// 與「查無資料」刻意分成兩件事：查無是正常結果（回 found=false），
/// 故障要讓端點回 502 讓呼叫端知道「這次不算數，稍後再試」——
/// 兩者混在一起的話，上游掛掉會被整批快取成「這些地址都查不到」。
/// </summary>
public sealed class GeocodeBackendException : Exception
{
    public GeocodeBackendException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>依 app setting <c>GEOCODE_BACKEND</c> 選後端（預設 NOMINATIM）。</summary>
public static class GeocodeBackendFactory
{
    public const string 設定鍵 = "GEOCODE_BACKEND";

    /// <summary>
    /// 後端是無狀態的（節流狀態是 static），建一次共用即可。
    ///
    /// <c>PublicationOnly</c> 不是隨手選的：預設的 <c>ExecutionAndPublication</c> **會把例外一起快取**，
    /// 於是 <c>GEOCODE_BACKEND</c> 設錯（或設 TGOS）之後，即使把 app setting 改對，
    /// 這個 Lazy 仍會永遠重丟同一個例外，非得重啟 Function App 不可——
    /// 那是「改了設定卻沒生效、還查不出原因」的典型（2026-09-03 審查 S4）。
    /// PublicationOnly 不快取例外，下一次請求就會重新讀設定；代價是併發時可能多建幾個後端物件，
    /// 而後端無狀態，多建幾個沒有副作用。
    /// </summary>
    private static readonly Lazy<IGeocodeBackend> _預設 = new(() => 建立(
        Environment.GetEnvironmentVariable(設定鍵)), LazyThreadSafetyMode.PublicationOnly);

    public static IGeocodeBackend 預設後端 => _預設.Value;

    public static IGeocodeBackend 建立(string? 名稱)
    {
        var n = string.IsNullOrWhiteSpace(名稱) ? "NOMINATIM" : 名稱!.Trim().ToUpperInvariant();
        return n switch
        {
            "NOMINATIM" => new NominatimBackend(),
            // TGOS 要等 TGOS 核准 ＋ NAT Gateway 固定出站 IP 才接得起來（計畫 §8.5）。
            // 這裡刻意丟例外而不是默默退回 Nominatim：設定寫了 TGOS 卻跑 Nominatim
            // 會讓人以為門牌精度已經生效，比開不起來難查太多。
            "TGOS" => throw new NotSupportedException("TGOS 後端尚未實作"),
            _ => throw new NotSupportedException($"未知的 {設定鍵}：{名稱}"),
        };
    }
}
