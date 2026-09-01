using Microsoft.Data.SqlClient;

namespace TS.API.ExtData;

/// <summary>驗證通過的呼叫端。</summary>
public sealed record ApiCaller(int ClientId, string 代號, string 名稱, IReadOnlyCollection<string> Datasets)
{
    public bool CanRead(string dataset) => Datasets.Contains(dataset, StringComparer.OrdinalIgnoreCase);
}

public static class ApiAuth
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// 驗金鑰。驗證規則全部在 DB 的 <c>extdata.usp_api_key_verify</c>：
    /// 金鑰雜湊比對、客戶是否啟用、金鑰是否撤銷或過期，以及可存取哪些資料集。
    /// 規則集中一處，才不會 API 與維護工具各判一套。
    /// 回 null＝驗不過（不細分原因，避免用回應內容幫人試金鑰）。
    /// </summary>
    public static async Task<ApiCaller?> VerifyAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        await using var cn = await CentralDb.OpenAsync(ct);
        await using var cmd = new SqlCommand("extdata.usp_api_key_verify", cn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
        };
        cmd.Parameters.AddWithValue("@api_key", apiKey);

        int clientId = 0;
        string 代號 = "", 名稱 = "";
        var datasets = new List<string>();

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            clientId = r.GetInt32(0);
            代號 = r.GetString(1);
            名稱 = r.GetString(2);
            // 沒開通任何資料集時 dataset 會是 NULL（proc 用 LEFT JOIN）——
            // 那代表「金鑰有效但什麼都不能拿」，要能與「金鑰無效」區分開（前者回 403 後者 401）
            if (!r.IsDBNull(3)) datasets.Add(r.GetString(3));
        }

        return clientId == 0 ? null : new ApiCaller(clientId, 代號, 名稱, datasets);
    }
}
