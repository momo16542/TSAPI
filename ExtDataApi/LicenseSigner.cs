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
    /// 2026-10-02 起快取「base64 解碼後的 PKCS#8 bytes」（以私鑰字串的 SHA-256 比對），
    /// 每次簽章照改版前**完全相同的程式路徑** <c>ECDsa.Create()</c>＋<c>ImportPkcs8PrivateKey</c>，
    /// 不引入新的平台相依 API——正式機是 Linux（OpenSSL），本機測試是 Windows（CNG），
    /// 改走別的匯入路徑（例如 ECParameters）沒在 Linux 實測過，一旦出錯是全客戶的授權簽章一起壞。
    /// 每次建新 ECDsa 實例而不共用：.NET 對 ECDsa 實例成員不保證 thread-safe；
    /// 快取的 byte[] 只讀不寫，併發讀取安全。換金鑰（雜湊不同）就重新解碼，不會拿舊鑰簽。
    /// </summary>
    public static string 簽章(string 私鑰Base64, string 被簽字串)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(取私鑰Pkcs8(私鑰Base64), out _);
        var 簽 = ecdsa.SignData(
            Encoding.UTF8.GetBytes(被簽字串),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(簽);
    }

    /// <summary>
    /// 暖機用：解碼私鑰（進快取）並對固定假資料簽一次，把 ECDsa／OpenSSL 的載入與 JIT 成本付掉。
    /// 簽出來的東西直接丟掉。私鑰未設定時回 false（不丟例外）。
    /// </summary>
    public static bool 預熱()
    {
        var 私鑰 = 讀私鑰Base64();
        if (私鑰 is null) return false;
        簽章(私鑰, 組被簽字串("warmup", string.Empty, "none", null, 0, "1970-01-01T00:00:00Z"));
        return true;
    }

    /// <summary>
    /// （私鑰字串雜湊, 解碼後 PKCS#8）成對存放，一次替換整組，讀的一方不會看到半新半舊。
    /// 刻意用 class 不用 record：record 自動產生的 ToString 會把欄位內容印出來。
    /// </summary>
    internal sealed class 私鑰快取項(byte[] 雜湊, byte[] pkcs8)
    {
        public byte[] 雜湊 { get; } = 雜湊;
        public byte[] Pkcs8 { get; } = pkcs8;
        public override string ToString() => "私鑰快取項(內容已遮蔽)";
    }

    private static 私鑰快取項? _私鑰快取;

    /// <summary>目前的快取項（測試用，確認 ToString 不外洩）。</summary>
    internal static 私鑰快取項? 目前快取 => Volatile.Read(ref _私鑰快取);

    private static byte[] 取私鑰Pkcs8(string 私鑰Base64)
    {
        var 雜湊 = SHA256.HashData(Encoding.UTF8.GetBytes(私鑰Base64));
        var 快取 = Volatile.Read(ref _私鑰快取);
        if (快取 is not null && 快取.雜湊.AsSpan().SequenceEqual(雜湊))
            return 快取.Pkcs8;

        var pkcs8 = Convert.FromBase64String(私鑰Base64);
        // 併發時可能兩條同時解碼、後寫的蓋掉先寫的——兩者內容相同，無害。
        Volatile.Write(ref _私鑰快取, new 私鑰快取項(雜湊, pkcs8));
        return pkcs8;
    }
}
