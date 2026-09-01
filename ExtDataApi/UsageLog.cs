using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace TS.API.ExtData;

/// <summary>
/// 用量紀錄。除了計費與除錯，最重要的用途是回答**「哪家客戶幾天沒來拉了」**——
/// 匯率管線的客戶端排程 2026-06-26 停掉、一個月沒人發現，就是因為上游每天成功、
/// 沒有任何地方看得出「有人不再來取資料」（ERPV2 memory: fxrate-auto-pipeline）。
///
/// 金鑰無效時 client_id 記 NULL 但仍然要寫——那才看得出設定錯誤或有人在試金鑰。
/// **寫紀錄失敗絕不能讓 API 回錯**：資料已經正確回給客戶了，日誌寫不進去是次要問題。
/// </summary>
public static class UsageLog
{
    public static async Task WriteAsync(ILogger logger, int? clientId, string? dataset, string? 參數,
        int? 回傳筆數, long 耗時毫秒, int 狀態碼, string? 備註 = null)
    {
        try
        {
            await using var cn = await CentralDb.OpenAsync();
            await using var cmd = new SqlCommand(
                @"INSERT extdata.api_usage (client_id, dataset, 參數, 回傳筆數, 耗時毫秒, 狀態碼, 備註)
                  VALUES (@c, @d, @p, @n, @ms, @s, @m)", cn);
            cmd.Parameters.AddWithValue("@c", (object?)clientId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@d", (object?)dataset ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p", (object?)Trim(參數, 400) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@n", (object?)回傳筆數 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", (int)耗時毫秒);
            cmd.Parameters.AddWithValue("@s", 狀態碼);
            cmd.Parameters.AddWithValue("@m", (object?)Trim(備註, 400) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "寫用量紀錄失敗（不影響回應）");
        }
    }

    private static string? Trim(string? s, int max)
        => s is null ? null : (s.Length <= max ? s : s[..max]);
}
