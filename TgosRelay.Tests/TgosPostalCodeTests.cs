using TgosRelay;

namespace TgosRelay.Tests;

/// <summary>
/// 去郵遞區號（2026-09-09 用禾久真實客戶地址實測後加的）。
/// TGOS QueryAddr 帶著開頭的郵遞區號一律查無，拿掉才命中門牌——
/// 禾久快取 5 筆裡有 3 筆是這種寫法，不處理等於切到 TGOS 後這些客戶從地圖上消失。
/// </summary>
[TestClass]
public class TgosPostalCodeTests
{
    [DataTestMethod]
    // 3 碼（實測案例）
    [DataRow("231新北市新店區中興路3段3號9樓", "新北市新店區中興路3段3號9樓")]
    [DataRow("830高雄市鳳山區青年路二段421號", "高雄市鳳山區青年路二段421號")]
    [DataRow("408台中市南屯區大墩六街350巷33號", "台中市南屯區大墩六街350巷33號")]
    // 5 碼／6 碼（3+2、3+3 郵遞區號）
    [DataRow("23141新北市新店區中興路3段3號", "新北市新店區中興路3段3號")]
    [DataRow("231416新北市新店區中興路3段3號", "新北市新店區中興路3段3號")]
    // 郵遞區號與地址之間有空白
    [DataRow("231 新北市新店區中興路3段3號", "新北市新店區中興路3段3號")]
    // 前導空白
    [DataRow("  231新北市新店區中興路3段3號", "新北市新店區中興路3段3號")]
    public void 開頭郵遞區號要被拿掉(string 輸入, string 期望) => Assert.AreEqual(期望, TgosClient.去郵遞區號(輸入));

    [DataTestMethod]
    // 沒有郵遞區號：原樣不動
    [DataRow("新北市新店區中興路3段3號9樓")]
    [DataRow("台中市南屯區大墩六街350巷33號")]
    // 長度不是 3/5/6 碼＝不是郵遞區號，不要亂砍（地址中間的數字更不能碰）
    [DataRow("12新北市新店區中興路3段3號")]
    [DataRow("1234新北市新店區中興路3段3號")]
    [DataRow("1234567新北市新店區中興路3段3號")]
    public void 不是郵遞區號的一律原樣(string 輸入) => Assert.AreEqual(輸入, TgosClient.去郵遞區號(輸入));

    [TestMethod]
    public void 空值與純數字不炸()
    {
        Assert.AreEqual(string.Empty, TgosClient.去郵遞區號(null!));
        Assert.AreEqual(string.Empty, TgosClient.去郵遞區號(""));
        // 純郵遞區號、後面沒有地址 → 原樣送出（讓 TGOS 自己回查無，不要送空字串）
        Assert.AreEqual("231", TgosClient.去郵遞區號("231"));
    }
}
