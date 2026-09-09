using System.Globalization;
using TS.API.ExtData;
using TS.API.ExtData.Geocode;

namespace TgosRelay.Tests;

/// <summary>
/// 反查（座標 → 地址）端點的純函式測試（2026-09-09）：罐頭 JSON 解析、格網化鍵、
/// 工廠選型、參數驗證。不連網、不連 DB——會連外的部分（HTTP 送出、快取讀寫）
/// 刻意留在 <c>NominatimReverseBackend.ReverseAsync</c> 與 <c>ReverseGeocodeCache</c> 的
/// 非靜態路徑上，這裡只測抽出來的靜態函式（同 <see cref="TgosRelayBackendTests"/> 的做法）。
/// </summary>
[TestClass]
public class NominatimReverseParseTests
{
    /// <summary>Nominatim /reverse 的典型回應（門牌層級）。</summary>
    private const string 門牌回應 = """
    {
      "place_id": 1, "licence": "ODbL",
      "lat": "25.0377", "lon": "121.5645",
      "category": "building", "type": "yes", "addresstype": "building",
      "display_name": "1號, 市府路, 信義區, 臺北市, 110204, 臺灣",
      "address": { "house_number": "1", "road": "市府路" }
    }
    """;

    [TestMethod]
    public void 門牌層級回應_取得地址與house精度()
    {
        var hit = NominatimReverseBackend.解析(門牌回應, "NOMINATIM");
        Assert.IsNotNull(hit);
        Assert.AreEqual("1號, 市府路, 信義區, 臺北市, 110204, 臺灣", hit!.Address);
        Assert.AreEqual("NOMINATIM", hit.Source);
        Assert.AreEqual(GeoPrecision.門牌, hit.Precision);
    }

    [TestMethod]
    public void 機關物件_降成路名精度()
    {
        // 「市府路1號」反查常常回的是 addresstype=office 的臺北市政府——那不是門牌層級。
        // 精度必須看回應物件的層級，不是地址字串裡有沒有「號」（GeoPrecision 的核心約定）。
        var hit = NominatimReverseBackend.解析("""
            {"addresstype":"office","type":"government","display_name":"臺北市政府, 市府路1號, 信義區"}
            """, "NOMINATIM");
        Assert.AreEqual(GeoPrecision.路名, hit!.Precision);
    }

    [TestMethod]
    public void 沒有addresstype_退type再退category()
    {
        var hit = NominatimReverseBackend.解析("""
            {"type":"house","display_name":"某處"}
            """, "NOMINATIM");
        Assert.AreEqual(GeoPrecision.門牌, hit!.Precision);

        var hit2 = NominatimReverseBackend.解析("""
            {"category":"highway","display_name":"某路"}
            """, "NOMINATIM");
        Assert.AreEqual(GeoPrecision.路名, hit2!.Precision);
    }

    [TestMethod]
    public void 上游回error_是查無不是故障()
    {
        // /reverse 查無時回 HTTP 200 + {"error":"Unable to geocode"}——那是正常結果，
        // 端點要回 found=false，不是 502。
        Assert.IsNull(NominatimReverseBackend.解析("""{"error":"Unable to geocode"}""", "NOMINATIM"));
    }

    [TestMethod]
    public void 沒有display_name_視同查無()
    {
        Assert.IsNull(NominatimReverseBackend.解析("""{"addresstype":"building"}""", "NOMINATIM"));
        Assert.IsNull(NominatimReverseBackend.解析("""{"display_name":"   "}""", "NOMINATIM"));
    }

    [TestMethod]
    public void 空回應_視同查無()
    {
        Assert.IsNull(NominatimReverseBackend.解析(null, "NOMINATIM"));
        Assert.IsNull(NominatimReverseBackend.解析("", "NOMINATIM"));
    }

    [TestMethod]
    public void 回應不是JSON_是上游故障()
    {
        // HTML 錯誤頁（被 CDN 擋、被限流）不可以被當成「查無」，
        // 否則上游掛掉會被整批快取成「這些座標都查不到地址」。
        Assert.ThrowsException<GeocodeBackendException>(() =>
            NominatimReverseBackend.解析("<html>429 Too Many Requests</html>", "NOMINATIM"));
    }

    [TestMethod]
    public void 回應是陣列_視同查無()
    {
        // /reverse 正常回單一物件；回陣列代表打錯端點或上游改版，不硬解。
        Assert.IsNull(NominatimReverseBackend.解析("[]", "NOMINATIM"));
    }
}

/// <summary>格網化快取鍵：反查快取的核心，錯了整個節流保護就不存在。</summary>
[TestClass]
public class ReverseGeocodeCacheKeyTests
{
    [TestMethod]
    public void 鍵是小數五位的lat逗號lon()
    {
        Assert.AreEqual("25.03770,121.56450", ReverseGeocodeCache.座標鍵(25.0377, 121.5645));
    }

    [TestMethod]
    public void 幾公尺內的多筆掃描_收斂成同一個鍵()
    {
        // 同一個工地的幾十支 TAG：末幾位小數不同，必須落在同一格，否則各打一次上游。
        var a = ReverseGeocodeCache.座標鍵(25.0377012, 121.5645004);
        var b = ReverseGeocodeCache.座標鍵(25.0377048, 121.5645049);
        Assert.AreEqual(a, b);
        Assert.AreEqual("25.03770,121.56450", a);
    }

    [TestMethod]
    public void 相距超過一格_不同鍵()
    {
        Assert.AreNotEqual(ReverseGeocodeCache.座標鍵(25.03770, 121.5645),
                           ReverseGeocodeCache.座標鍵(25.03772, 121.5645));
    }

    [TestMethod]
    public void 負座標與零_格式不跑掉()
    {
        Assert.AreEqual("-33.86880,-151.20930", ReverseGeocodeCache.座標鍵(-33.8688, -151.2093));
        Assert.AreEqual("0.00000,0.00000", ReverseGeocodeCache.座標鍵(0, 0));
    }

    [TestMethod]
    public void 鍵長度不超過欄位上限()
    {
        // 最長的情況：兩個負號 + 三位整數 + 五位小數
        var 最長 = ReverseGeocodeCache.座標鍵(-89.123456, -179.123456);
        Assert.IsTrue(最長.Length <= ReverseGeocodeCache.鍵長度上限,
            $"鍵長 {最長.Length} 超過欄位上限 {ReverseGeocodeCache.鍵長度上限}：{最長}");
    }

    [TestMethod]
    public void 不受地區設定影響()
    {
        // de-DE 的小數點是逗號：沒有 InvariantCulture 的話鍵會變成 "25,03770,121,56450"，
        // 與既有列永遠對不上（而且不會報錯，只會每次都 miss）。
        var 原 = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.AreEqual("25.03770,121.56450", ReverseGeocodeCache.座標鍵(25.0377, 121.5645));
        }
        finally { Thread.CurrentThread.CurrentCulture = 原; }
    }

    [TestMethod]
    public void TTL政策與正查共用()
    {
        // 讀同一個 TGOS_CACHE_HOURS：TGOS 的授權條款管的是「能不能長期重製內政部資料」，
        // 與正查／反查無關，各寫一份就會出現「正查已依裁決不留、反查還留著」的漏洞。
        Assert.IsNull(ReverseGeocodeCache.找到保留時數("NOMINATIM"));
        Assert.IsTrue(ReverseGeocodeCache.可寫快取("NOMINATIM"));
        try
        {
            Environment.SetEnvironmentVariable(GeocodeCache.設定鍵_Tgos保留時數, "0");
            Assert.AreEqual(TimeSpan.Zero, ReverseGeocodeCache.找到保留時數("TGOS"));
            Assert.IsFalse(ReverseGeocodeCache.可寫快取("TGOS"));
        }
        finally { Environment.SetEnvironmentVariable(GeocodeCache.設定鍵_Tgos保留時數, null); }
    }
}

/// <summary>反查後端工廠：設定鍵、預設值、TGOS 尚未支援的訊息要指名原因。</summary>
[TestClass]
public class ReverseGeocodeBackendFactoryTests
{
    [TestMethod]
    public void 預設與空字串_都是NOMINATIM()
    {
        Assert.IsInstanceOfType(ReverseGeocodeBackendFactory.建立(null), typeof(NominatimReverseBackend));
        Assert.IsInstanceOfType(ReverseGeocodeBackendFactory.建立("  "), typeof(NominatimReverseBackend));
        Assert.AreEqual("NOMINATIM", ReverseGeocodeBackendFactory.建立(" nominatim ").來源);
    }

    // TGOS 反查 2026-09-09 起已支援（轉發器開出 /reverse），
    // 選型與缺設定的行為改在 TgosReverseRelayBackendTests.工廠_TGOS不再是NotSupported 驗。

    [TestMethod]
    public void 未知值_丟NotSupported並指名設定鍵()
    {
        // 訊息不指名設定鍵的話，維運者只看得到 500 而不知道要改哪個 app setting。
        var ex = Assert.ThrowsException<NotSupportedException>(() => ReverseGeocodeBackendFactory.建立("GOOGLE"));
        StringAssert.Contains(ex.Message, ReverseGeocodeBackendFactory.設定鍵);
        StringAssert.Contains(ex.Message, "GOOGLE");
    }

    [TestMethod]
    public void 設定鍵名稱凍結()
        => Assert.AreEqual("REVERSE_BACKEND", ReverseGeocodeBackendFactory.設定鍵);
}

/// <summary>端點的參數驗證：手持機送進來的東西什麼都有，這一關是唯一的守門。</summary>
[TestClass]
public class ReverseGeocodeParameterTests
{
    [TestMethod]
    public void 正常座標_通過()
    {
        var (lat, lon, err) = ReverseGeocodeFunctions.驗參數("25.0377", "121.5645");
        Assert.IsNull(err);
        Assert.AreEqual(25.0377, lat, 1e-9);
        Assert.AreEqual(121.5645, lon, 1e-9);
    }

    [TestMethod]
    public void 零零_被擋掉()
    {
        // 手持機沒定位到時兩欄都是 0。放行會反查出大西洋，把「這筆沒有座標」的事實洗掉。
        var (_, _, err) = ReverseGeocodeFunctions.驗參數("0", "0");
        Assert.IsNotNull(err);
        StringAssert.Contains(err!, "0");
    }

    [TestMethod]
    public void 只有一邊是零_不擋()
    {
        // 赤道（lat=0）與本初子午線（lon=0）上是有陸地的，只有兩者同時為 0 才是哨兵值。
        Assert.IsNull(ReverseGeocodeFunctions.驗參數("0", "121.5645").錯誤);
        Assert.IsNull(ReverseGeocodeFunctions.驗參數("25.0377", "0").錯誤);
    }

    [DataTestMethod]
    [DataRow(null, "121.5")]
    [DataRow("", "121.5")]
    [DataRow("   ", "121.5")]
    public void 缺lat_訊息指名lat(string? lat, string lon)
    {
        var (_, _, err) = ReverseGeocodeFunctions.驗參數(lat, lon);
        StringAssert.Contains(err!, "lat");
    }

    [TestMethod]
    public void 缺lon_訊息指名lon()
        => StringAssert.Contains(ReverseGeocodeFunctions.驗參數("25.0", null).錯誤!, "lon");

    [DataTestMethod]
    [DataRow("abc")]
    [DataRow("25,0377")]     // de-DE 寫法：查詢字串固定用 InvariantCulture，不接受
    [DataRow("NaN")]         // TryParse 吃得下，但與任何數字比大小都是 false → 範圍檢查會漏
    [DataRow("Infinity")]
    public void 不是有效數字_被擋掉(string lat)
    {
        var (_, _, err) = ReverseGeocodeFunctions.驗參數(lat, "121.5");
        Assert.IsNotNull(err, $"「{lat}」應該被擋掉");
        StringAssert.Contains(err!, "lat");
    }

    [DataTestMethod]
    [DataRow("90.1", "121.5")]
    [DataRow("-90.1", "121.5")]
    [DataRow("25.0", "180.1")]
    [DataRow("25.0", "-180.1")]
    public void 超出範圍_被擋掉(string lat, string lon)
        => Assert.IsNotNull(ReverseGeocodeFunctions.驗參數(lat, lon).錯誤);

    [DataTestMethod]
    [DataRow("90", "180")]
    [DataRow("-90", "-180")]
    public void 邊界值_放行(string lat, string lon)
        => Assert.IsNull(ReverseGeocodeFunctions.驗參數(lat, lon).錯誤);

    [TestMethod]
    public void 前後空白_容忍()
        => Assert.IsNull(ReverseGeocodeFunctions.驗參數(" 25.0377 ", " 121.5645 ").錯誤);
}
