using System.Globalization;
using Microsoft.Data.SqlClient;

namespace TS.API.ExtData.Geocode;

/// <summary>快取中的一筆反查結果。</summary>
/// <param name="找到">上一次反查有沒有查到；false＝上游確實查無（不是「還沒查過」）。</param>
public sealed record ReverseGeocodeCacheRow(
    string 座標鍵, double? Lat, double? Lon, bool 找到, string? Address,
    string? Source, string? Precision, DateTime 建立時間);

/// <summary>
/// 中央反查快取（extdata.reverse_geocode_cache）。行為與 <see cref="GeocodeCache"/> 完全一致
/// （查無也寫、TTL、來源比對），差別只有一個：**鍵是格網化後的座標**，見 <see cref="座標鍵"/>。
///
/// 為什麼要中央快取：Nominatim 的 1 req/s 是**所有客戶加起來**的上限
/// （而且正查與反查共用，見 <see cref="NominatimThrottle"/>），
/// 而 TAG 掃描的座標高度重疊——同一個工地、同一個水泥廠的地磅站，每天都會被掃到。
///
/// **只存「座標→地址」，不存誰問的**（沿用 2026-09-03 使用者對正查快取的裁決）：
/// 跨客戶共用的是座標與地址這個公開事實，不是「哪家客戶在這個工地施工」——
/// 後者是客戶的工地名單，不該因為共用快取而外洩到別家客戶的查詢命中率裡。
/// 誰查了什麼在 extdata.api_usage 有紀錄，兩者分開。
/// </summary>
public static class ReverseGeocodeCache
{
    /// <summary>座標鍵的小數位數。5 位 ≈ 1.1 公尺（緯度方向），見 <see cref="座標鍵"/>。</summary>
    public const int 格網小數位 = 5;

    /// <summary>快取鍵長度上限（＝ extdata.reverse_geocode_cache.座標鍵 的 varchar(30)）。</summary>
    public const int 鍵長度上限 = 30;

    /// <summary>地址欄長度上限（＝ extdata.reverse_geocode_cache.地址 的 nvarchar(300)）。</summary>
    public const int 地址長度上限 = 300;

    /// <summary>
    /// 查無（找到=0）的保留天數。與正查同一個值、同一個理由：查無也要快取，
    /// 否則海上／山區那種永遠查不到的座標會每次都打上游；但**不能永久**，
    /// 上游的資料會補進來（新開的路、新編的門牌），過了就再問一次。
    /// </summary>
    public const int 查無保留天數 = GeocodeCache.查無保留天數;

    /// <summary>
    /// 座標 → 快取鍵：lat/lon 各四捨五入到小數 <see cref="格網小數位"/> 位（≈1 公尺），
    /// 組成 <c>"{lat:F5},{lon:F5}"</c>（一律 <see cref="CultureInfo.InvariantCulture"/>）。
    ///
    /// **為什麼要格網化**（不是最佳化，是必需）：同一個工地會有幾十支 TAG 掃在幾公尺內，
    /// 手持機每一次定位的末幾位小數都不一樣。不格網化的話，這幾十筆會是幾十個不同的鍵，
    /// 每一支都打一次上游——而 Nominatim 的 1 req/s 是所有客戶、正查反查加總共用的，
    /// 一車 TAG 匯進來就足以把當天的預算吃光，其他客戶的定位跟著排隊。
    /// 1 公尺的格網對「補施工地址」這個用途沒有任何損失（門牌本身就不只 1 公尺寬）。
    ///
    /// **F5 是四捨五入不是截斷**，所以 25.037649 與 25.037651 會落在相鄰兩格——
    /// 格網化本來就會有邊界，接受它：邊界兩側各打一次上游，回來的地址一樣，
    /// 而追求「絕對不重複」需要的是空間索引，代價遠大於偶爾多一次呼叫。
    ///
    /// ⚠️ 這個鍵**只給快取用**。API 回給呼叫端的 lat/lon 一律是**原值**
    /// （呼叫端傳什麼回什麼），見 <c>ReverseGeocodeFunctions</c>——
    /// 回格網化值會讓呼叫端以為自己送出的座標被「修正」了。
    /// </summary>
    public static string 座標鍵(double lat, double lon)
        => 格網緯度(lat).ToString("F5", CultureInfo.InvariantCulture)
         + "," + 格網經度(lon).ToString("F5", CultureInfo.InvariantCulture);

    /// <summary>格網化後的緯度（寫進快取列的值；回應仍用原值）。</summary>
    public static double 格網緯度(double lat) => Math.Round(lat, 格網小數位, MidpointRounding.AwayFromZero);

    /// <summary>格網化後的經度（寫進快取列的值；回應仍用原值）。</summary>
    public static double 格網經度(double lon) => Math.Round(lon, 格網小數位, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 「找到」列依來源的保留時間，**直接沿用正查的政策**（<see cref="GeocodeCache.找到保留時數"/>，
    /// 讀同一個 <c>TGOS_CACHE_HOURS</c>）：TGOS 的授權條款管的是「內政部資料能不能被長期重製」，
    /// 與資料是正查還反查無關。各寫一份就會出現「正查已依裁決不留、反查還留著」的漏洞。
    /// </summary>
    public static TimeSpan? 找到保留時數(string? 來源) => GeocodeCache.找到保留時數(來源);

    /// <summary>這個來源的結果可不可以寫進中央快取（TGOS 保留時數 0＝不寫）。</summary>
    public static bool 可寫快取(string? 來源) => GeocodeCache.可寫快取(來源);

    /// <summary>
    /// 查快取。回 null＝未命中，三種情況：沒這筆／過期的「查無」／**這筆是別的後端寫的**。
    /// 命中時順手累計命中次數與最後命中時間。
    /// </summary>
    /// <param name="目前來源">
    /// 目前生效的後端來源代碼（<see cref="IReverseGeocodeBackend.來源"/>）。快取列的 <c>來源</c>
    /// 與它不同時視同未命中，讓呼叫端重打上游，並由 <see cref="UpsertAsync"/> 整列覆蓋。
    ///
    /// **為什麼一定要有這個**（同正查 2026-09-03 審查 S3）：中央快取存在的目的就是
    /// 「之後換 TGOS 拿門牌精度」。不比對來源的話，換了 TGOS 之後，先前被 Nominatim
    /// 寫成 road-fallback 的列會被無限期命中，TGOS 一次都不會被打到，而且**完全沒有訊號**。
    /// 傳 null／空字串＝不比對（治具與一次性維運腳本用）。
    /// </param>
    public static async Task<ReverseGeocodeCacheRow?> TryGetAsync(SqlConnection cn, string 座標鍵,
        string? 目前來源 = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(座標鍵)) return null;

        const string sql = @"SELECT 座標鍵, 緯度, 經度, 找到, 地址, 來源, 精度, 建立時間
                             FROM extdata.reverse_geocode_cache WHERE 座標鍵 = @k";
        ReverseGeocodeCacheRow? row = null;

        await using (var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 })
        {
            cmd.Parameters.AddWithValue("@k", 座標鍵);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                row = new ReverseGeocodeCacheRow(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetDouble(1),
                    r.IsDBNull(2) ? null : r.GetDouble(2),
                    r.GetBoolean(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetDateTime(7));
            }
        }

        if (row is null) return null;

        // 過期的「查無」＝視同未命中，讓呼叫端再問一次上游
        if (!row.找到 && row.建立時間 < DateTime.UtcNow.AddDays(-查無保留天數)) return null;

        // 「找到」但來源有保留上限（TGOS 短效去重）且已過期＝視同未命中
        if (row.找到 && 找到保留時數(row.Source) is { } 上限 && row.建立時間 < DateTime.UtcNow - 上限) return null;

        // 別的後端寫的＝視同未命中。命中次數也不累計——這一筆對目前的後端來說根本沒發揮作用。
        if (!string.IsNullOrWhiteSpace(目前來源)
            && !string.Equals(row.Source ?? string.Empty, 目前來源!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await 記命中(cn, 座標鍵, ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// 寫入或更新一筆（找到與查無都寫）。
    /// 沒有 DELETE：這張表的授權刻意只有 SELECT/INSERT/UPDATE（部署腳本內有驗證段）。
    /// </summary>
    public static async Task UpsertAsync(SqlConnection cn, string 座標鍵, double lat, double lon,
        string? 地址, string 來源, string? 精度, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(座標鍵)) return;

        // 先 UPDATE 再 INSERT（不用 MERGE：MERGE 在高並行下的已知問題多，而這裡只有兩種情況）。
        // 建立時間一併更新＝「這筆內容最後一次向上游確認的時間」，查無的 TTL 靠它算。
        const string sql = @"
UPDATE extdata.reverse_geocode_cache
   SET 緯度 = @lat, 經度 = @lon, 找到 = @f, 地址 = @a, 來源 = @src, 精度 = @prec,
       建立時間 = SYSUTCDATETIME()
 WHERE 座標鍵 = @k;
IF @@ROWCOUNT = 0
   INSERT extdata.reverse_geocode_cache (座標鍵, 緯度, 經度, 找到, 地址, 來源, 精度)
   VALUES (@k, @lat, @lon, @f, @a, @src, @prec);";

        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@k", 座標鍵);
        // 寫進去的是**格網化後**的座標：它才是這一列代表的位置（鍵就是從它來的）。
        cmd.Parameters.AddWithValue("@lat", 格網緯度(lat));
        cmd.Parameters.AddWithValue("@lon", 格網經度(lon));
        cmd.Parameters.AddWithValue("@f", !string.IsNullOrWhiteSpace(地址));
        cmd.Parameters.AddWithValue("@a", (object?)Trim(地址, 地址長度上限) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@src", (object?)Trim(來源, 20) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prec", (object?)Trim(精度, 30) ?? DBNull.Value);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 兩個實例同時反查同一格、同時走到 INSERT：後到的撞主鍵。
            // 這不是錯誤——別人已經寫進去了，內容一樣。吞掉。
        }
    }

    private static async Task 記命中(SqlConnection cn, string 座標鍵, CancellationToken ct)
    {
        const string sql = @"UPDATE extdata.reverse_geocode_cache
                                SET 命中次數 = 命中次數 + 1, 最後命中 = SYSUTCDATETIME()
                              WHERE 座標鍵 = @k";
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@k", 座標鍵);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? Trim(string? s, int max)
        => s is null ? null : (s.Length <= max ? s : s[..max]);
}
