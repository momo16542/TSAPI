using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

namespace TS.API.ExtData;

/// <summary>
/// 中央庫連線。連線字串裡**沒有帳密**：Azure 上用受控識別，本機退回 az login 的身分。
///
/// 取權杖刻意自己 try/catch 而不用 DefaultAzureCredential／ChainedTokenCredential：
/// 那兩個只在憑證回報「不可用」時才往下試，而裝了 Azure Arc 代理程式的機器上
/// 受控識別是「可用但失敗」（讀 Arc 權杖檔被權限擋下），會直接中斷整條鏈
/// （2026-09-01 實測，ERPV2 memory: azure-sql-auth-from-dev-machine）。
/// </summary>
public static class CentralDb
{
    private static readonly string[] Scope = { "https://database.windows.net/.default" };

    public static string Server =>
        Environment.GetEnvironmentVariable("CENTRAL_SQL_SERVER")
        ?? throw new InvalidOperationException("CENTRAL_SQL_SERVER 環境變數未設定");

    public static string Database =>
        Environment.GetEnvironmentVariable("CENTRAL_SQL_DATABASE") ?? "YanyueCentral";

    public static async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var cn = new SqlConnection($"Server={Server};Database={Database};Encrypt=True;Connect Timeout=30")
        {
            AccessToken = GetToken(),
        };
        await cn.OpenAsync(ct);
        return cn;
    }

    private static string GetToken()
    {
        var errors = new List<string>();

        // 使用者指派的受控識別要靠 AZURE_CLIENT_ID 指定；參數空的只認系統指派。
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        TokenCredential 受控 = string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential()
            : new ManagedIdentityCredential(clientId);

        foreach (var (name, credential) in new (string, TokenCredential)[]
                 {
                     ("受控識別", 受控),
                     ("Azure CLI", new AzureCliCredential()),
                 })
        {
            try
            {
                return credential.GetToken(new TokenRequestContext(Scope), CancellationToken.None).Token;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}：{ex.Message.Split('\n')[0]}");
            }
        }
        throw new InvalidOperationException("取不到中央庫存取權杖。嘗試過：" + string.Join("｜", errors));
    }
}
