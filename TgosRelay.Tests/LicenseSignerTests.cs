using System.Security.Cryptography;
using System.Text;
using TS.API.ExtData;

namespace TgosRelay.Tests;

/// <summary>
/// 授權簽章（2026-10-02 私鑰解析改程序內快取後補）。
/// 驗簽邏輯逐字照 ERPV2 <c>LogicBll/License/LicenseSignature.cs</c>：
/// <c>ImportSubjectPublicKeyInfo</c> ＋ <c>VerifyData(SHA256, IeeeP1363FixedFieldConcatenation)</c>。
/// ECDSA 簽章每次都帶隨機數，兩種載入方式簽出的位元組本來就不會相同——
/// 相容性的判準是「同一把公鑰都驗得過」，不是位元組相等。
/// </summary>
[TestClass]
public class LicenseSignerTests
{
    /// <summary>ERP 內嵌的正式公鑰（ERPV2 LicenseSignature.公鑰）。</summary>
    private const string 正式公鑰 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmE8/VvsCKsva/ztt8G/dniB7/wZqp0T5qNE4ymWyHdpAC2zIOmLClQn4pP0rlH9mxfyLqmFiFN6xlo9No9pXjw==";

    /// <summary>ERPV2 LicenseGateTests.正式向量的簽章（2026-09-11 以正式私鑰簽出，含 ediid）。</summary>
    private const string 正式向量簽章 =
        "cJa1pjuVBv3S357pe5EYZAhqN3f7STQIkcUp5+kwo2/dp4QK0hqwgt+Earj5P6uJZavkY5E8omKXohSvlOsRKw==";

    /// <summary>ERP 端驗簽（照抄 LicenseSignature.驗簽 的密碼學部分）。</summary>
    private static bool ERP驗簽(string 公鑰Base64, string 被簽字串, string 簽章Base64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(公鑰Base64), out _);
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(被簽字串), Convert.FromBase64String(簽章Base64),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>改版前的簽法（每次匯入 PKCS#8），用來對照。</summary>
    private static string 舊版簽章(string 私鑰Base64, string 被簽字串)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(私鑰Base64), out _);
        return Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(被簽字串),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static (string 私鑰, string 公鑰) 自產金鑰()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(k.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    }

    [TestMethod]
    public void 組被簽字串_與ERP契約向量相同_正式公鑰驗得過()
    {
        var s = LicenseSigner.組被簽字串("nogi01", "twvzqr69h6", "active", "2027-09-05", 60, "2026-09-05T06:47:12Z");
        Assert.AreEqual("nogi01|twvzqr69h6|active|2027-09-05|60|2026-09-05T06:47:12Z", s);
        Assert.IsTrue(ERP驗簽(正式公鑰, s, 正式向量簽章));
    }

    [TestMethod]
    public void 快取版與舊版簽章_同一公鑰都驗得過_且為P1363固定64位元組()
    {
        var (私鑰, 公鑰) = 自產金鑰();
        var s = LicenseSigner.組被簽字串("nogi01", "twvzqr69h6", "active", "2027-09-05", 60, "2026-10-02T01:02:03Z");

        var 新 = LicenseSigner.簽章(私鑰, s);   // 第一次：解析並進快取
        var 新2 = LicenseSigner.簽章(私鑰, s);  // 第二次：走快取
        var 舊 = 舊版簽章(私鑰, s);

        foreach (var 簽 in new[] { 新, 新2, 舊 })
        {
            Assert.AreEqual(64, Convert.FromBase64String(簽).Length);
            Assert.IsTrue(ERP驗簽(公鑰, s, 簽));
        }
        // 被簽字串動一個字就要驗不過（確認不是什麼都放行）
        Assert.IsFalse(ERP驗簽(公鑰, s.Replace("2027", "2099"), 新2));
    }

    [TestMethod]
    public void 換私鑰_不會沿用舊鑰快取()
    {
        var (私鑰A, 公鑰A) = 自產金鑰();
        var (私鑰B, 公鑰B) = 自產金鑰();
        const string s = "c|e|active|2027-01-01|60|2026-10-02T00:00:00Z";

        var 簽A = LicenseSigner.簽章(私鑰A, s);
        var 簽B = LicenseSigner.簽章(私鑰B, s);

        Assert.IsTrue(ERP驗簽(公鑰A, s, 簽A));
        Assert.IsTrue(ERP驗簽(公鑰B, s, 簽B));
        Assert.IsFalse(ERP驗簽(公鑰A, s, 簽B));
    }

    [TestMethod]
    public void 併發簽章_每一筆都驗得過()
    {
        var (私鑰, 公鑰) = 自產金鑰();
        var 結果 = new bool[200];
        Parallel.For(0, 結果.Length, i =>
        {
            var s = $"c{i}||active||60|2026-10-02T00:00:00Z";
            結果[i] = ERP驗簽(公鑰, s, LicenseSigner.簽章(私鑰, s));
        });
        Assert.IsTrue(結果.All(x => x));
    }

    [TestMethod]
    public void 私鑰快取項ToString_不印出私鑰()
    {
        var (私鑰, _) = 自產金鑰();
        LicenseSigner.簽章(私鑰, "x");
        var 快取 = LicenseSigner.目前快取;
        Assert.IsNotNull(快取);
        var 字串 = 快取.ToString();
        Assert.IsFalse(字串.Contains(私鑰));
        Assert.IsFalse(字串.Contains(Convert.ToBase64String(快取.Pkcs8)));
        Assert.AreEqual("私鑰快取項(內容已遮蔽)", 字串);
    }
}
