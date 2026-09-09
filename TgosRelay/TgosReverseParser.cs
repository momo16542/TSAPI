using System.Globalization;
using System.Text.Json;

namespace TgosRelay;

/// <summary>
/// 一筆反查命中。<c>Raw</c> 是命中物件的原樣 JSON，給拿到真金鑰後定欄位對映用（YYAPI 不必理它）。
/// </summary>
/// <param name="Address">反查到的地址（顯示用字串）。<c>includeVillage=false</c>（預設）時已去掉里／鄰。</param>
/// <param name="Lon">命中物件上的經度（TGOS 回的門牌點，不是輸入的座標）。回應沒有座標欄時為 null。</param>
/// <param name="Lat">同上的緯度。</param>
/// <param name="MatchType">像 match／score 的欄位，給精度判定當線索；沒有就 null。</param>
/// <param name="Dis"><c>DIS</c> 欄：距查詢點的公尺數。回應沒有這欄時為 null（呼叫端不能因此當成故障，見 <see cref="RelayConfig.GeoMaxDistanceM"/>）。</param>
public sealed record TgosReverseHit(string Address, double? Lon, double? Lat, string? MatchType, double? Dis, JsonElement Raw);

/// <summary>
/// 解析「坐標回傳門牌服務」（<c>GeoQueryAddr.asmx/PointQueryNearAddr</c>）的回應。
///
/// ⚠️ **回應欄位名未知**：官方規格書只列參數表、沒有回應範例，核准前也打不出真回應
/// （假金鑰只會回一句 <c>Length of the data to decrypt is invalid.</c>）。
/// 所以這支寫成**容忍式**：遞迴找第一個「欄名看起來像地址」（含 <c>ADDR</c> 或 <c>FULL</c>，不分大小寫）
/// 且值非空白的字串欄，把它所在的物件當成命中，順手撈出 X/Y 與任何像 match／score 的欄位。
/// 拿到真金鑰、看過真回應之後再收斂成明確欄名（同 <see cref="TgosResponseParser"/> 當初面對 QueryAddr 的處境）。
///
/// 「查無」與「上游故障」刻意分開：找不到地址欄 → 回 null（查無，端點回 found=false）；
/// 剝掉 ASMX 的 &lt;string&gt; 外殼之後根本不是 JSON → 丟 <see cref="TgosUpstreamException"/>（502），
/// 因為那代表金鑰錯／服務改版／被擋，把它當「查無」會讓錯誤被靜靜吞掉。
/// </summary>
public static class TgosReverseParser
{
    /// <param name="回應">TGOS 反查的原始回應（ASMX 外殼或裸 JSON）。</param>
    /// <param name="xySwap">X/Y 對調旗標，見 <see cref="RelayConfig.GeoXySwap"/>。</param>
    /// <param name="includeVillage">
    /// false（預設；對齊 <see cref="RelayConfig.GeoIncludeVillage"/> 的預設值）＝從命中物件的
    /// <c>FULL_ADDR</c>（或其他被判為地址欄的欄位）移除 <c>VILLAGE</c>／<c>NEIGHBORHOOD</c> 原樣字串。
    /// true＝地址原樣回傳。
    /// </param>
    public static TgosReverseHit? Parse(string? 回應, bool xySwap = false, bool includeVillage = false)
    {
        var json = TgosResponseParser.剝XML外殼(回應);
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json!);
        }
        catch (JsonException ex)
        {
            // 例：金鑰錯時回的 "Length of the data to decrypt is invalid."（純文字，不是 JSON）
            throw new TgosUpstreamException($"TGOS 反查回應不是 JSON：{截短(json!)}", ex);
        }

        using (doc)
        {
            var 命中 = 找第一個含地址的物件(doc.RootElement);
            if (命中 == null) return null;

            var (e, 地址) = 命中.Value;
            double? lon = null, lat = null;
            if (取數字(e, "X", out var x) && 取數字(e, "Y", out var y))
            {
                lon = xySwap ? y : x;
                lat = xySwap ? x : y;
            }
            double? dis = 取數字(e, "DIS", out var d) ? d : null;
            if (!includeVillage) 地址 = 去除里鄰(地址, e);

            return new TgosReverseHit(地址, lon, lat, 找精度線索(e), dis, e.Clone());
        }
    }

    /// <summary>
    /// 遞迴找第一個帶「地址欄」的物件，回傳該物件與地址字串。
    /// 深度優先、先看物件自己的欄位再往下鑽：巢狀回應（<c>{"Data":{"AddressList":[{…}]}}</c>）
    /// 與直接陣列（<c>[{…}]</c>）都吃得下。
    /// </summary>
    private static (JsonElement 物件, string 地址)? 找第一個含地址的物件(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (!像地址欄名(p.Name)) continue;
                    var v = 取字串值(p.Value);
                    if (!string.IsNullOrWhiteSpace(v)) return (e, v!.Trim());
                }
                foreach (var p in e.EnumerateObject())
                {
                    var r = 找第一個含地址的物件(p.Value);
                    if (r != null) return r;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var it in e.EnumerateArray())
                {
                    var r = 找第一個含地址的物件(it);
                    if (r != null) return r;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>欄名含 ADDR 或 FULL 就當地址欄（FULL_ADDR／ADDRESS／FULLADDRESS／Addr…）。</summary>
    private static bool 像地址欄名(string 名)
        => 名.Contains("ADDR", StringComparison.OrdinalIgnoreCase)
        || 名.Contains("FULL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 從地址字串移除 <c>VILLAGE</c>（里）與 <c>NEIGHBORHOOD</c>（鄰）兩段原樣字串，不自己重組地址。
    ///
    /// 為什麼不重組：<c>SECTION</c> 回的是 <c>"5"</c>，但 <c>FULL_ADDR</c> 裡是「五段」——
    /// 自己組要做數字轉中文、還要處理巷/弄/衖的先後順序，出錯機會遠大於效益；
    /// <c>FULL_ADDR</c> 是 TGOS 自己組好的權威字串，直接在它身上做字串移除最安全。
    ///
    /// 例：<c>臺北市信義區三張里34鄰信義路五段100號</c>（VILLAGE=三張里 NEIGHBORHOOD=34鄰）
    /// → <c>臺北市信義區信義路五段100號</c>。
    /// </summary>
    private static string 去除里鄰(string 地址, JsonElement e)
    {
        var 里 = 取字串(e, "VILLAGE");
        var 鄰 = 取字串(e, "NEIGHBORHOOD");
        var 有移除 = false;
        var 結果 = 地址;
        if (!string.IsNullOrWhiteSpace(里)) { 結果 = 結果.Replace(里!.Trim(), ""); 有移除 = true; }
        if (!string.IsNullOrWhiteSpace(鄰)) { 結果 = 結果.Replace(鄰!.Trim(), ""); 有移除 = true; }
        // 移除後理論上不會留下空白（中文地址本來就沒有空格），保守處理避免有例外情況殘留連續空白
        if (有移除)
            結果 = string.Join(' ', 結果.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return 結果;
    }

    /// <summary>
    /// 精度線索：先找慣用欄名，再退成「欄名含 MATCH／SCORE 的第一個純量欄」。
    /// 值域未知，這裡只負責把它原樣帶出去，判定留給 YYAPI 端（拿到真值域後收斂）。
    /// </summary>
    private static string? 找精度線索(JsonElement e)
    {
        var 直取 = 取字串(e, "MATCH_TYPE") ?? 取字串(e, "MATCHTYPE") ?? 取字串(e, "SCORE");
        if (直取 != null) return 直取;

        foreach (var p in e.EnumerateObject())
        {
            if (!p.Name.Contains("MATCH", StringComparison.OrdinalIgnoreCase)
                && !p.Name.Contains("SCORE", StringComparison.OrdinalIgnoreCase)) continue;
            var v = 取字串值(p.Value);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static string? 取字串值(JsonElement v)
        => v.ValueKind == JsonValueKind.String ? v.GetString()
         : v.ValueKind == JsonValueKind.Number ? v.ToString()
         : null;

    private static bool 取數字(JsonElement e, string 名, out double 值)
    {
        值 = 0;
        if (!e.TryGetProperty(名, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out 值),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out 值),
            _ => false,
        };
    }

    private static string? 取字串(JsonElement e, string 名)
        => e.TryGetProperty(名, out var v) ? 取字串值(v) : null;

    private static string 截短(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "…";
}
