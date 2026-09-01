using Azure.Core;
using Azure.Identity;

namespace ExtDataJob;

/// <summary>
/// 取 Azure SQL 的存取權杖：先試受控識別（容器內），失敗就退回 Azure CLI（開發機）。
///
/// **為什麼是自己寫 try/catch 而不是用 DefaultAzureCredential 或 ChainedTokenCredential**
/// （2026-09-01 兩者都實測失敗，見 ERPV2 memory: azure-sql-auth-from-dev-machine）：
///   開發機裝了 Azure Arc 代理程式，受控識別「可用但會失敗」——讀 Arc 權杖檔
///   `C:\ProgramData\AzureConnectedMachineAgent\Tokens\*.key` 被權限擋下。
///   而那兩個內建鏈**只在憑證回報「不可用」(CredentialUnavailableException) 時才往下試**；
///   這種「可用但失敗」(AuthenticationFailedException) 會直接中斷整條鏈，
///   永遠輪不到已經 az login 的 Azure CLI。
///   所以這裡攔所有例外再退回，行為才可預期。
/// </summary>
internal static class AzureToken
{
    private static readonly string[] Scope = { "https://database.windows.net/.default" };

    public static string ForSqlDatabase()
    {
        var errors = new List<string>();

        // Container Apps 這邊用**使用者指派**的受控識別（沿用 botrate-job 的做法），
        // 靠 AZURE_CLIENT_ID 指定是哪一個。參數空的 ManagedIdentityCredential() 只認系統指派，
        // 在使用者指派的環境永遠取不到權杖——本機測不出來，部署上去才炸。
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
                // 不印堆疊：這裡的失敗多半是「這個環境沒有這種身分」，屬正常流程
                errors.Add($"{name}：{ex.Message.Split('\n')[0]}");
            }
        }

        throw new InvalidOperationException(
            "取不到 Azure SQL 存取權杖。容器內請確認已指派受控識別並在 DB 建立對應使用者；" +
            "開發機請先 az login。嘗試過：" + string.Join("｜", errors));
    }
}
