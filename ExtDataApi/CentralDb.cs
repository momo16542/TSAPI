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
///
/// 2026-10-02 credential 改成程序內共用（原本每次 OpenAsync 都 new 一個）：
/// Azure.Identity 的權杖快取是**掛在 credential 實例上**的，每次 new 等於每次丟掉快取；
/// 而官方保證 credential 的所有方法 thread-safe、建議重複使用同一實例
/// （Azure.Identity 1.13.1 README「Thread safety」「Token caching」兩節；
/// CHANGELOG 1.8.0：ManagedIdentityCredential 內建權杖快取，呼叫端不必自己快取）。
/// 受控識別失敗時把該實例丟掉、下次重建——保留原本「失敗後每次都重新嘗試」的行為，
/// 不讓一次暫時性失敗被快取成永久失敗。
/// </summary>
public static class CentralDb
{
    private static readonly string[] Scope = { "https://database.windows.net/.default" };

    private static readonly object 建立鎖 = new();
    private static TokenCredential? _受控識別;
    private static AzureCliCredential? _azureCli;

    public static string Server =>
        Environment.GetEnvironmentVariable("CENTRAL_SQL_SERVER")
        ?? throw new InvalidOperationException("CENTRAL_SQL_SERVER 環境變數未設定");

    public static string Database =>
        Environment.GetEnvironmentVariable("CENTRAL_SQL_DATABASE") ?? "YanyueCentral";

    public static async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var cn = new SqlConnection($"Server={Server};Database={Database};Encrypt=True;Connect Timeout=30")
        {
            AccessToken = await GetTokenAsync(),
        };
        try
        {
            await cn.OpenAsync(ct);
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
        return cn;
    }

    /// <summary>
    /// 取中央庫存取權杖（受控識別優先，失敗退 az login）。
    /// public 是給暖機端點單獨量「取權杖」這一段的耗時；一般呼叫端用 <see cref="OpenAsync"/>。
    /// 不吃呼叫端的 CancellationToken（與改版前相同）：取權杖是程序共用的快取動作，
    /// 不該因為某一個請求被取消而中斷。
    /// </summary>
    public static async Task<string> GetTokenAsync()
    {
        var errors = new List<string>();

        var 受控 = 取受控識別();
        try
        {
            return (await 受控.GetTokenAsync(new TokenRequestContext(Scope), CancellationToken.None)).Token;
        }
        catch (Exception ex)
        {
            // 丟掉這個實例，下次重建（只換掉「還是它」的那一個，避免併發時把別人新建的也丟掉）
            Interlocked.CompareExchange(ref _受控識別, null, 受控);
            errors.Add($"受控識別：{ex.Message.Split('\n')[0]}");
        }

        try
        {
            return (await 取AzureCli().GetTokenAsync(new TokenRequestContext(Scope), CancellationToken.None)).Token;
        }
        catch (Exception ex)
        {
            errors.Add($"Azure CLI：{ex.Message.Split('\n')[0]}");
        }

        throw new InvalidOperationException("取不到中央庫存取權杖。嘗試過：" + string.Join("｜", errors));
    }

    private static TokenCredential 取受控識別()
    {
        var c = Volatile.Read(ref _受控識別);
        if (c is not null) return c;
        lock (建立鎖)
        {
            // 使用者指派的受控識別要靠 AZURE_CLIENT_ID 指定；參數空的只認系統指派。
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            return _受控識別 ??= string.IsNullOrWhiteSpace(clientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(clientId);
        }
    }

    private static AzureCliCredential 取AzureCli()
    {
        var c = Volatile.Read(ref _azureCli);
        if (c is not null) return c;
        lock (建立鎖)
        {
            return _azureCli ??= new AzureCliCredential();
        }
    }
}
