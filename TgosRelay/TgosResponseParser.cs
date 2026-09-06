using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace TgosRelay;

/// <summary>一筆 TGOS 命中。<c>Raw</c> 是命中物件的原樣 JSON，給 S5 實測時定精度對映用（YYAPI 不必理它）。</summary>
public sealed record TgosHit(double Lon, double Lat, string? MatchType, string? FullAddress, JsonElement Raw);

/// <summary>
/// 解析 QueryAddr 回應。邏輯自 ERPV2 <c>TsErp.Geo/Geocoding/TgosGeocodeProvider.cs</c> 搬來（兩 repo 不互相參考，各留一份）。
/// ASMX 的 HTTP POST 端點會把 JSON 包在 &lt;string&gt; 元素裡回傳，所以先剝 XML 外殼再解 JSON；
/// 回應結構各版本略有差異（AddressList／Data／直接陣列），遞迴找第一個同時有 X 與 Y 的物件。
/// </summary>
public static class TgosResponseParser
{
    public static TgosHit? Parse(string? 回應, bool xySwap = false)
    {
        var json = 剝XML外殼(回應);
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        var 節點 = 找第一個含XY的物件(doc.RootElement);
        if (節點 == null) return null;

        var e = 節點.Value;
        if (!取數字(e, "X", out var x) || !取數字(e, "Y", out var y)) return null;

        var lon = xySwap ? y : x;
        var lat = xySwap ? x : y;
        var matchType = 取字串(e, "MATCH_TYPE") ?? 取字串(e, "MATCHTYPE") ?? 取字串(e, "SCORE");
        var full = 取字串(e, "FULL_ADDR") ?? 取字串(e, "FULLADDR") ?? 取字串(e, "ADDRESS");
        return new TgosHit(lon, lat, matchType, full, e.Clone());
    }

    private static string? 剝XML外殼(string? 回應)
    {
        if (string.IsNullOrWhiteSpace(回應)) return null;
        var s = 回應.Trim();
        if (!s.StartsWith('<')) return s;
        try { return XDocument.Parse(s).Root?.Value?.Trim(); }
        catch (System.Xml.XmlException) { return null; }
    }

    private static JsonElement? 找第一個含XY的物件(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("X", out _) && e.TryGetProperty("Y", out _)) return e;
                foreach (var p in e.EnumerateObject())
                {
                    var r = 找第一個含XY的物件(p.Value);
                    if (r != null) return r;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var it in e.EnumerateArray())
                {
                    var r = 找第一個含XY的物件(it);
                    if (r != null) return r;
                }
                return null;
            default:
                return null;
        }
    }

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
    {
        if (!e.TryGetProperty(名, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString()
             : v.ValueKind == JsonValueKind.Number ? v.ToString()
             : null;
    }
}
