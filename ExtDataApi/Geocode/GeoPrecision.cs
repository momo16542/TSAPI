namespace TS.API.ExtData.Geocode;

/// <summary>
/// 精度（<c>precision</c>）的**唯一判定處**。API 合約只有兩個值：
/// <list type="bullet">
///   <item><see cref="門牌"/>（<c>house</c>）＝上游回的是門牌／建物層級的物件，呼叫端可以照字面信任。</item>
///   <item><see cref="路名"/>（<c>road-fallback</c>）＝其他一切情況：退到路名層級查到的、
///         或上游雖然對完整地址有回應但那筆物件只是路段／機關／地標。</item>
/// </list>
///
/// **為什麼要判物件層級而不是「輸入有沒有門牌號」**（2026-09-03 審查 S1）：
/// Nominatim 對台灣完整門牌的命中率很差，「台北市信義區市府路1號」常常回的是
/// 「臺北市政府」這種 <c>addresstype=office</c> 的機關物件——輸入有門牌號不代表回來的是門牌。
/// 若只看輸入就標成 <c>house</c>，整個「house 可信任」的合約就是假的，而且**沒有任何訊號**。
/// 判斷依據一律是回應本身的 <c>addresstype</c>（退 <c>type</c>、再退 <c>category</c>，
/// 取法與 ERPV2 <c>TsErp.Geo/Geocoding/NominatimGeocodeProvider.解析</c> 相同）。
///
/// 名單刻意保守：不在名單上的值一律降成 <see cref="路名"/>。
/// 少報精度只是讓畫面顯示「路名層級」（使用者自己會去看地圖），
/// 多報精度會讓人以為點在門牌上——寧可低報。
///
/// ⚠️ 這份判定在 ERPV2 有一份**內容相同的複本**（<c>TsErp.Geo/Geocoding/GeoPrecision.cs</c>）。
///    刻意複製而不是引用：TSAPI 不參考 ERPV2 組件，兩邊各自部署。改一邊要順手改另一邊。
/// </summary>
public static class GeoPrecision
{
    /// <summary>門牌／建物層級命中。</summary>
    public const string 門牌 = "house";

    /// <summary>路名層級（含「退一階查到的」與「回應物件不是門牌」兩種情況）。</summary>
    public const string 路名 = "road-fallback";

    /// <summary>
    /// 算門牌層級的 <c>addresstype</c>／<c>type</c> 值。
    /// <c>house</c>／<c>house_number</c>＝門牌點；<c>building</c>／<c>yes</c>＝建物輪廓
    /// （OSM 的 <c>building=yes</c> 在舊版 Nominatim 的 <c>type</c> 就是字面上的 <c>yes</c>）；
    /// <c>address</c>＝地址點。其餘（road／residential／office／amenity／place…）都不是門牌。
    /// </summary>
    private static readonly HashSet<string> 門牌層級值 = new(StringComparer.OrdinalIgnoreCase)
    {
        "house", "house_number", "houses", "building", "address", "yes",
    };

    /// <summary>回應物件的層級是不是門牌／建物。null／空字串＝不是。</summary>
    public static bool 是門牌層級(string? 物件層級)
        => !string.IsNullOrWhiteSpace(物件層級) && 門牌層級值.Contains(物件層級!.Trim());

    /// <summary>
    /// 判定要寫進 <c>precision</c> 的值。
    /// </summary>
    /// <param name="物件層級">回應的 <c>addresstype</c>（沒有就退 <c>type</c>、<c>category</c>）。</param>
    /// <param name="允許門牌">
    /// 這一次查詢有沒有資格拿到門牌精度。false＝查的是**去掉門牌號後**的降級地址
    /// （或輸入本來就沒門牌號），不管回應說自己是什麼層級，結果都只到路名。
    /// </param>
    public static string 判定(string? 物件層級, bool 允許門牌 = true)
        => 允許門牌 && 是門牌層級(物件層級) ? 門牌 : 路名;
}
