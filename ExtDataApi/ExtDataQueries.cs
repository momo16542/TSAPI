using Microsoft.Data.SqlClient;

namespace TS.API.ExtData;

/// <summary>一頁查詢結果。</summary>
public sealed record PagedRows(List<Dictionary<string, object?>> Data, long? NextCursor)
{
    public int Count => Data.Count;
}

/// <summary>
/// 三個資料集的查詢。**刻意抽出 HTTP 層**：這樣治具能直接呼叫真正的查詢程式碼，
/// 而不是在測試裡複製一份 SQL——複製的那份驗過了也不代表產品是對的
/// （ERPV2 memory: harness-must-call-real-code）。
///
/// 分頁游標＝上一頁最後一列的 pkid。用 pkid 而不是 OFFSET：
/// 資料是持續累積的，OFFSET 在中途有新增時會漏列或重複。
/// </summary>
public static class ExtDataQueries
{
    public const int DefaultLimit = 5000;
    public const int MaxLimit = 20000;

    public static async Task<PagedRows> Ttvma(SqlConnection cn, DateTime? since, string? vehicle,
        int limit, long cursor, CancellationToken ct = default)
    {
        const string sql = @"SELECT TOP (@limit) pkid, 資料月份, 車輛別, 銷售別, 期別, 規格, 廠牌,
                                    數量, 列序, 廠牌序, 來源檔, 抓取時間
                             FROM extdata.ttvma_sales
                             WHERE pkid > @cursor
                               AND (@since IS NULL OR 資料月份 >= @since)
                               AND (@vehicle IS NULL OR 車輛別 = @vehicle)
                             ORDER BY pkid";
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@cursor", cursor);
        cmd.Parameters.AddWithValue("@since", (object?)since ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vehicle", (object?)vehicle ?? DBNull.Value);
        return await ReadAsync(cmd, limit, ct);
    }

    public static async Task<PagedRows> MetalPrice(SqlConnection cn, DateTime? since, string? item,
        int limit, long cursor, CancellationToken ct = default)
    {
        const string sql = @"SELECT TOP (@limit) pkid, 日期, 金屬, 名稱, 交易所, 品號, 報價單位,
                                    收盤價, 漲跌, 漲跌百分比, 開盤, 最高, 最低, 成交量, 未平倉, 抓取時間
                             FROM extdata.metal_price
                             WHERE pkid > @cursor
                               AND (@since IS NULL OR 日期 >= @since)
                               AND (@item IS NULL OR 品號 = @item)
                             ORDER BY pkid";
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@cursor", cursor);
        cmd.Parameters.AddWithValue("@since", (object?)since ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@item", (object?)item ?? DBNull.Value);
        return await ReadAsync(cmd, limit, ct);
    }

    public static async Task<PagedRows> MetalItem(SqlConnection cn, int limit, long cursor,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT TOP (@limit) pkid, 品號, 金屬, 交易所, 名稱, 報價單位, 對應品號,
                                    是否啟用, 備註, 更新時間
                             FROM extdata.metal_item
                             WHERE pkid > @cursor
                             ORDER BY pkid";
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@cursor", cursor);
        return await ReadAsync(cmd, limit, ct);
    }

    private static async Task<PagedRows> ReadAsync(SqlCommand cmd, int limit, CancellationToken ct)
    {
        var list = new List<Dictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(r.FieldCount);
            for (int i = 0; i < r.FieldCount; i++)
            {
                // **DBNull 一律轉 null，不可轉 0**：公會數量欄的空格代表「沒有這個組合」
                // 不是「賣 0 台」；行情沒報價同理。這條語意從解析、寫入一路守到 API 回應。
                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            }
            list.Add(row);
        }

        // 滿一頁才給下一頁游標；不滿代表已是最後一頁，回 null 讓呼叫端停止
        long? next = list.Count == limit && list.Count > 0 ? Convert.ToInt64(list[^1]["pkid"]) : null;
        return new PagedRows(list, next);
    }
}
