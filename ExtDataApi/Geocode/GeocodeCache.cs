using Microsoft.Data.SqlClient;

namespace TS.API.ExtData.Geocode;

/// <summary>快取中的一筆定位結果。</summary>
/// <param name="找到">上一次查詢有沒有查到；false＝上游確實查無（不是「還沒查過」）。</param>
public sealed record GeocodeCacheRow(
    string 地址鍵, string 地址, bool 找到, double? Lon, double? Lat,
    string? Source, string? Precision, DateTime 建立時間);

/// <summary>
/// 中央 geocode 快取（extdata.geocode_cache）。
///
/// 為什麼要中央快取：Nominatim 的 1 req/s 是**所有客戶加起來**的上限，
/// 而各客戶的客戶／廠商地址高度重疊（同一個工業區、同一棟大樓）。
/// 快取放中央，第二家客戶問同一個地址就不必再打上游。
///
/// **只存「地址→座標」，不存誰問的**（2026-09-03 使用者裁決）：
/// 跨客戶共用的是地址與座標這個公開事實，不是「哪家客戶有這個地址」——
/// 後者是客戶名單，不該因為共用快取而外洩到別家客戶的查詢命中率裡。
/// 誰查了什麼在 extdata.api_usage 有紀錄（各客戶各自的列），兩者分開。
/// </summary>
public static class GeocodeCache
{
    /// <summary>
    /// 查無（找到=0）的保留天數。查無也要快取，否則一個打錯的地址會每次都打上游；
    /// 但**不能永久**——地址可能是新開的路、上游資料也會補進來，過了就再問一次。
    ///
    /// 找到的**沒有時間上的到期**（座標不會變；地址本身改了就是另一個鍵），
    /// 但也**不是永久有效**：<see cref="TryGetAsync"/> 會比對「寫這筆的來源」與「目前的後端」，
    /// 不同就視同未命中（理由見該方法的 目前來源 參數）。
    /// </summary>
    public const int 查無保留天數 = 7;

    /// <summary>
    /// 查快取。回 null＝未命中，三種情況：沒這筆／過期的「查無」／**這筆是別的後端寫的**。
    /// 命中時順手累計命中次數與最後命中時間（看得出哪些地址值得預熱、也看得出快取有沒有在發揮作用）。
    /// </summary>
    /// <param name="目前來源">
    /// 目前生效的後端來源代碼（<c>IGeocodeBackend.來源</c>：NOMINATIM／將來的 TGOS）。
    /// 快取列的 <c>來源</c> 與它不同時視同未命中，讓呼叫端重打上游，並由 <see cref="UpsertAsync"/>
    /// 覆蓋掉舊列（座標／來源／精度／找到／建立時間整列改寫）。
    ///
    /// **為什麼一定要有這個**（2026-09-03 審查 S3）：中央快取存在的目的就是「之後換 TGOS 拿門牌精度」。
    /// 不比對來源的話，換了 TGOS 之後，先前被 Nominatim 寫成 road-fallback 的列會被無限期命中，
    /// TGOS 一次都不會被打到，而且**完全沒有訊號**——設定換了卻沒生效、還沒人看得出來。
    /// 傳 null／空字串＝不比對（治具與一次性維運腳本用）。
    /// </param>
    public static async Task<GeocodeCacheRow?> TryGetAsync(SqlConnection cn, string 地址鍵,
        string? 目前來源 = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(地址鍵)) return null;

        const string sql = @"SELECT 地址鍵, 地址, 找到, 經度, 緯度, 來源, 精度, 建立時間
                             FROM extdata.geocode_cache WHERE 地址鍵 = @k";
        GeocodeCacheRow? row = null;

        await using (var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 })
        {
            cmd.Parameters.AddWithValue("@k", 地址鍵);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                row = new GeocodeCacheRow(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetBoolean(2),
                    r.IsDBNull(3) ? null : r.GetDouble(3),
                    r.IsDBNull(4) ? null : r.GetDouble(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetDateTime(7));
            }
        }

        if (row is null) return null;

        // 過期的「查無」＝視同未命中，讓呼叫端再問一次上游
        if (!row.找到 && row.建立時間 < DateTime.UtcNow.AddDays(-查無保留天數)) return null;

        // 別的後端寫的＝視同未命中（換 TGOS 之後 Nominatim 的舊列要被重查覆蓋，不是永遠命中）。
        // 命中次數也不累計——這一筆對目前的後端來說根本沒發揮作用。
        if (!string.IsNullOrWhiteSpace(目前來源)
            && !string.Equals(row.Source ?? string.Empty, 目前來源!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await 記命中(cn, 地址鍵, ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// 寫入或更新一筆（找到與查無都寫）。
    /// 沒有 DELETE：這張表的授權刻意只有 SELECT/INSERT/UPDATE（腳本內有驗證段）。
    /// </summary>
    public static async Task UpsertAsync(SqlConnection cn, string 地址鍵, string 地址,
        double? lon, double? lat, string 來源, string? 精度, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(地址鍵)) return;

        // 先 UPDATE 再 INSERT（不用 MERGE：MERGE 在高並行下的已知問題多，而這裡只有兩種情況）。
        // 建立時間一併更新＝「這筆內容最後一次向上游確認的時間」，查無的 TTL 靠它算，
        // 否則一個查不到的地址會在 7 天後每次都重打上游。
        const string sql = @"
UPDATE extdata.geocode_cache
   SET 地址 = @a, 找到 = @f, 經度 = @lon, 緯度 = @lat, 來源 = @src, 精度 = @prec,
       建立時間 = SYSUTCDATETIME()
 WHERE 地址鍵 = @k;
IF @@ROWCOUNT = 0
   INSERT extdata.geocode_cache (地址鍵, 地址, 找到, 經度, 緯度, 來源, 精度)
   VALUES (@k, @a, @f, @lon, @lat, @src, @prec);";

        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@k", 地址鍵);
        cmd.Parameters.AddWithValue("@a", Trim(地址, AddressKey.長度上限));
        cmd.Parameters.AddWithValue("@f", lon.HasValue && lat.HasValue);
        cmd.Parameters.AddWithValue("@lon", (object?)lon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lat", (object?)lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@src", (object?)Trim(來源, 20) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prec", (object?)Trim(精度, 30) ?? DBNull.Value);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 兩個實例同時查同一個地址、同時走到 INSERT：後到的撞主鍵。
            // 這不是錯誤——別人已經寫進去了，內容一樣。吞掉。
        }
    }

    private static async Task 記命中(SqlConnection cn, string 地址鍵, CancellationToken ct)
    {
        const string sql = @"UPDATE extdata.geocode_cache
                                SET 命中次數 = 命中次數 + 1, 最後命中 = SYSUTCDATETIME()
                              WHERE 地址鍵 = @k";
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@k", 地址鍵);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? Trim(string? s, int max)
        => s is null ? null : (s.Length <= max ? s : s[..max]);
}
