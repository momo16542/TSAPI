using System.Text;

namespace TS.API.ExtData.Geocode;

/// <summary>
/// 地址 → 中央快取鍵的正規化（純函式，無相依，可直接被治具呼叫）。
///
/// 為什麼要正規化：同一個門牌在各客戶的主檔裡寫法不一定相同
/// （全形數字、「臺」與「台」、中間有沒有空白），字面直接當鍵會讓同一個地址
/// 被當成好幾筆、各打一次上游——而 Nominatim 的 1 req/s 是硬性上限，
/// 每一次沒必要的呼叫都是在花光大家的額度。
///
/// 規則（順序有意義）：
///   ① Trim
///   ② 全形英數與符號（U+FF01–U+FF5E）→ 半形；全形空白（U+3000）視為空白
///   ③ 去掉**所有**空白字元（半形、全形、Tab）
///   ④ 「臺」→「台」（臺北/台北 是同一個地方，官方與民間各寫各的）
///   ⑤ 上限 200 字（＝快取表 地址鍵 欄位長度；超過就截斷）
///
/// 刻意**不做**的事：不刪門牌號、不刪「之N」、不刪樓層。
/// 「中山路12之1號」與「中山路12號」是不同門牌，合併會定位到錯的地方；
/// 退到路名層級是 <see cref="NominatimBackend"/> 查無時才做的降級，不是鍵的職責。
/// </summary>
public static class AddressKey
{
    /// <summary>快取鍵長度上限（＝ extdata.geocode_cache.地址鍵 的 nvarchar(200)）。</summary>
    public const int 長度上限 = 200;

    /// <summary>把地址正規化成快取鍵（含截斷）。null／空白回空字串（呼叫端應在此之前就擋掉）。</summary>
    public static string 正規化(string? 地址)
    {
        var s = 正規化未截斷(地址);
        return s.Length <= 長度上限 ? s : s[..長度上限];
    }

    /// <summary>
    /// 「地址太長」的判準：量的是**正規化後**的長度，不是使用者送來的原字串。
    /// 兩邊必須用同一把尺（2026-09-03 審查 S9）——鍵是去掉所有空白之後才截到 200 的，
    /// 若用原字串長度擋，一個 205 字、去空白後 198 字的地址會被 400 擋掉，
    /// 但它其實完整放得進快取鍵、根本沒有超過任何東西。
    /// </summary>
    public static bool 超過長度上限(string? 地址) => 正規化未截斷(地址).Length > 長度上限;

    /// <summary>正規化的①～④步，**不做**第⑤步的截斷。長度判斷與截斷都以它為準。</summary>
    public static string 正規化未截斷(string? 地址)
    {
        if (string.IsNullOrWhiteSpace(地址)) return string.Empty;

        var sb = new StringBuilder(地址!.Length);
        foreach (var ch in 地址.Trim())
        {
            // 全形空白與一般空白：整個丟掉（③）
            if (ch == '　' || char.IsWhiteSpace(ch)) continue;

            var c = ch;
            // 全形英數與標點 → 半形（②）。U+FF01–U+FF5E 與 ASCII 0x21–0x7E 差 0xFEE0。
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);
            // 臺→台（④）
            if (c == '臺') c = '台';

            sb.Append(c);
        }

        return sb.ToString();
    }
}
