using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TS.API.ExtData;

/// <summary>
/// 授權回應的簽章（2026-09-05）。契約：.claude/tmp/license-contract.md（ERPV2 repo）。
///
/// 為什麼要簽：客戶端把授權狀態快取在 %ProgramData%\TsERP\license.json，
/// 沒有簽章的話使用者只要把快取檔的到期日改掉就能無限延用——
/// 快取是為了「離線也能開程式」而存在，不能同時變成繞過授權的後門。
/// 公鑰內嵌在客戶端程式常數裡，私鑰只在 Function App 的環境變數，
/// 所以偽造快取需要拿到私鑰，改客戶端程式常數則需要改可執行檔（另有簽章保護）。
///
/// 演算法選 ECDSA P-256＋SHA-256、簽章格式 IEEE P1363（r||s 固定 64 bytes）：
/// 簽章短（base64 88 字）、.NET 兩側原生支援、不需要 DER 解析。
/// **兩側都必須用 IeeeP1363FixedFieldConcatenation**——預設的 Rfc3279DerSequence
/// 長度不固定且位元組完全不同，混用會一律驗不過（而且錯誤訊息只是「false」）。
/// </summary>
public static class LicenseSigner
{
    /// <summary>私鑰所在的 app setting 名稱；值＝base64(PKCS#8)。</summary>
    public const string 私鑰環境變數 = "LICENSE_SIGNING_KEY";

    /// <summary>簽發時間的格式（UTC、秒精度、尾 Z）。'Z' 加引號＝當字面字元，不是格式指定字元。</summary>
    public const string 簽發時間格式 = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>到期日的格式（純日期）。</summary>
    public const string 到期日格式 = "yyyy-MM-dd";

    /// <summary>
    /// 組出被簽的字串（UTF-8、無 BOM）：
    /// <c>{client}|{ediid}|{狀態}|{到期日 or 空字串}|{寬限天數}|{簽發時間}</c>。
    /// 例：<c>nogi01|twvzqr69h6|active|2027-09-05|60|2026-09-05T06:47:12Z</c>
    ///
    /// 獨立成 public 方法是為了**兩側可以拿同一組固定測試向量對答案**：
    /// 客戶端驗簽失敗時，第一個要排除的就是「兩邊組字串的規則不一樣」
    /// （少一個分隔符、到期日補了 null 字樣、時間帶了毫秒——外觀都看不出來）。
    /// 狀態 none 時到期日為 null，這裡固定換成空字串（不是字面 "null"）。
    ///
    /// 2026-09-11 契約變更：第二段插入 <c>ediid</c>（交換識別）。ediid 由中央核發、
    /// 客戶端寫回 codata.ediid 當 B2B 對外身分——放進被簽字串，客戶端與中間人就都改不了。
    /// 尚未核發時是空字串。⚠️ 格式一改新舊客戶端互不相容；授權心跳尚未發版，現在改成本為零。
    /// </summary>
    public static string 組被簽字串(string client, string ediid, string 狀態, string? 到期日, int 寬限天數, string 簽發時間)
        => string.Join('|',
            client,
            ediid ?? string.Empty,
            狀態,
            到期日 ?? string.Empty,
            寬限天數.ToString(CultureInfo.InvariantCulture),
            簽發時間);

    /// <summary>讀私鑰；未設定時回 null（呼叫端要回 500，不可以無簽章放行）。</summary>
    public static string? 讀私鑰Base64()
    {
        var v = Environment.GetEnvironmentVariable(私鑰環境變數);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>
    /// 用 PKCS#8 私鑰簽一段字串，回 base64 的 IEEE P1363 簽章。
    ///
    /// 2026-10-02 起私鑰解析結果在程序內快取（原本每次都 base64 解碼＋ImportPkcs8PrivateKey）。
    /// 快取的是**解析好的 <see cref="ECParameters"/>**、每次簽章 <c>ECDsa.Create(參數)</c> 建新實例，
    /// 而不是快取一個 ECDsa 實例共用：.NET 文件對 ECDsa 實例成員不保證 thread-safe，
    /// 共用實例就得加鎖讓所有簽章排隊；ECParameters 只是幾個 byte[]，建好後只讀不寫，
    /// 併發讀取安全。快取以私鑰字串本身為鍵——app setting 換了金鑰（換金鑰演練）就重新解析，
    /// 不會拿舊鑰簽。簽章演算法、格式（P1363）、被簽字串全部不變，客戶端公鑰照驗。
    /// </summary>
    public static string 簽章(string 私鑰Base64, string 被簽字串)
    {
        using var ecdsa = ECDsa.Create(取私鑰參數(私鑰Base64));
        var 簽 = ecdsa.SignData(
            Encoding.UTF8.GetBytes(被簽字串),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(簽);
    }

    /// <summary>
    /// 暖機用：解析私鑰（進快取）並對固定假資料簽一次，把 ECDsa／OpenSSL 的首用成本付掉。
    /// 簽出來的東西直接丟掉。私鑰未設定時回 false（不丟例外）。
    /// </summary>
    public static bool 預熱()
    {
        var 私鑰 = 讀私鑰Base64();
        if (私鑰 is null) return false;
        簽章(私鑰, 組被簽字串("warmup", string.Empty, "none", null, 0, "1970-01-01T00:00:00Z"));
        return true;
    }

    /// <summary>（私鑰字串, 解析結果）成對存放，一次替換整組，讀的一方不會看到半新半舊。</summary>
    private sealed record 私鑰快取項(string 私鑰Base64, ECParameters 參數);

    private static 私鑰快取項? _私鑰快取;

    private static ECParameters 取私鑰參數(string 私鑰Base64)
    {
        var 快取 = Volatile.Read(ref _私鑰快取);
        if (快取 is not null && string.Equals(快取.私鑰Base64, 私鑰Base64, StringComparison.Ordinal))
            return 快取.參數;

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(私鑰Base64), out _);
        var 參數 = ecdsa.ExportParameters(includePrivateParameters: true);
        // 併發時可能兩條同時解析、後寫的蓋掉先寫的——兩者內容相同，無害。
        Volatile.Write(ref _私鑰快取, new 私鑰快取項(私鑰Base64, 參數));
        return 參數;
    }
}
