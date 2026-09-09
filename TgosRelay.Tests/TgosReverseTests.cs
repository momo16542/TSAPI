using System.Globalization;
using System.Net;
using TgosRelay;
using TS.API.ExtData.Geocode;

namespace TgosRelay.Tests;

/// <summary>
/// 轉發器端反查解析（2026-09-09）。核准前拿不到真回應，欄位名未知，所以解析器寫成**容忍式**：
/// 這裡的罐頭資料刻意用三種不同的欄名／結構，證明「換一種欄名就解不出來」不會發生。
/// 拿到真金鑰後把真實欄名補成第一個測試案例，再考慮收斂。
/// </summary>
[TestClass]
public class TgosReverseParserTests
{
    [TestMethod]
    public void 欄名FULL_ADDR_取地址與座標()
    {
        var hit = TgosReverseParser.Parse(
            """{"AddressList":[{"FULL_ADDR":"臺北市信義區市府路1號","X":121.5645,"Y":25.0377,"MATCH_TYPE":"完全比對"}]}""");
        Assert.IsNotNull(hit);
        Assert.AreEqual("臺北市信義區市府路1號", hit!.Address);
        Assert.AreEqual(121.5645, hit.Lon!.Value, 1e-6);
        Assert.AreEqual(25.0377, hit.Lat!.Value, 1e-6);
        Assert.AreEqual("完全比對", hit.MatchType);
        Assert.AreEqual("完全比對", hit.Raw.GetProperty("MATCH_TYPE").GetString());
    }

    [TestMethod]
    public void 欄名ADDRESS_也認得()
    {
        var hit = TgosReverseParser.Parse("""[{"ADDRESS":"新北市板橋區文化路一段1號","X":"121.46","Y":"25.01"}]""");
        Assert.AreEqual("新北市板橋區文化路一段1號", hit!.Address);
        Assert.AreEqual(121.46, hit.Lon!.Value, 1e-9);  // 字串型座標也要解得出來
    }

    [TestMethod]
    public void 巢狀物件_遞迴找得到()
    {
        var hit = TgosReverseParser.Parse(
            """{"Data":{"Result":{"Rows":[{"fullAddr":"臺中市西屯區台灣大道三段99號","X":120.64,"Y":24.16}]}}}""");
        Assert.AreEqual("臺中市西屯區台灣大道三段99號", hit!.Address);
    }

    [TestMethod]
    public void ASMX的XML外殼會先剝掉()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><string xmlns=\"http://tempuri.org/\">"
                  + System.Security.SecurityElement.Escape("""{"FULL_ADDR":"臺北市信義區市府路1號","X":121.5645,"Y":25.0377}""")
                  + "</string>";
        Assert.AreEqual("臺北市信義區市府路1號", TgosReverseParser.Parse(xml)!.Address);
    }

    [TestMethod]
    public void 沒有座標欄也算命中_地址才是主角()
    {
        // 反查要的是地址；座標欄是附帶線索，缺了不該讓整筆變成「查無」
        var hit = TgosReverseParser.Parse("""{"FULL_ADDR":"臺北市信義區市府路1號"}""");
        Assert.AreEqual("臺北市信義區市府路1號", hit!.Address);
        Assert.IsNull(hit.Lon);
        Assert.IsNull(hit.Lat);
    }

    [TestMethod]
    public void 欄名像match或score的也撿來當精度線索()
    {
        var hit = TgosReverseParser.Parse("""{"FULL_ADDR":"臺北市信義區市府路1號","MatchScore":95}""");
        Assert.AreEqual("95", hit!.MatchType);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("[]")]
    [DataRow("""{"AddressList":[]}""")]
    [DataRow("""{"Message":"查無資料"}""")]                       // 沒有地址欄
    [DataRow("""{"FULL_ADDR":"   "}""")]                          // 有地址欄但值是空白
    [DataRow("<string xmlns=\"x\">[]</string>")]
    public void 找不到地址_回null代表查無(string body)
    {
        Assert.IsNull(TgosReverseParser.Parse(body));
    }

    [TestMethod]
    public void 剝殼後不是JSON_是上游故障不是查無()
    {
        // 金鑰錯（或拿正查金鑰打反查）時 TGOS 回的就是這句純文字。
        // 當成「查無」會被寫進快取變成「這個座標沒有地址」，錯誤從此靜音。
        var xml = "<string xmlns=\"http://tempuri.org/\">Length of the data to decrypt is invalid.</string>";
        var ex = Assert.ThrowsException<TgosUpstreamException>(() => TgosReverseParser.Parse(xml));
        StringAssert.Contains(ex.Message, "decrypt");

        // 沒有 XML 外殼的裸文字同理
        Assert.ThrowsException<TgosUpstreamException>(() => TgosReverseParser.Parse("Length of the data to decrypt is invalid."));
    }

    [TestMethod]
    public void XY對調開關()
    {
        var hit = TgosReverseParser.Parse(
            """{"FULL_ADDR":"某處","X":121.5645,"Y":25.0377}""", xySwap: true);
        Assert.AreEqual(25.0377, hit!.Lon!.Value, 1e-6);
        Assert.AreEqual(121.5645, hit.Lat!.Value, 1e-6);
    }
}

/// <summary>
/// 送出端：oPX/oPY 的擺法（含 <c>TGOS_GEO_XY_SWAP</c>）與座標格式化。
/// 用假的 HttpMessageHandler 攔下請求本文，不連網。
/// </summary>
[TestClass]
public class TgosClientReverseTests
{
    private sealed class 攔截Handler(string 回應) : HttpMessageHandler
    {
        public string? 收到的本文;
        public Uri? 收到的Url;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            收到的Url = request.RequestUri;
            收到的本文 = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(回應) };
        }
    }

    private const string 罐頭 = """{"FULL_ADDR":"臺北市信義區市府路1號","X":121.5645,"Y":25.0377}""";

    private static (TgosClient 用戶端, 攔截Handler 攔截) 建立(bool geoXySwap)
    {
        var h = new 攔截Handler(罐頭);
        var cfg = new RelayConfig { TgosGeoAppId = "app", TgosGeoApiKey = "key", GeoXySwap = geoXySwap };
        return (new TgosClient(new HttpClient(h), cfg), h);
    }

    [TestMethod]
    public async Task 預設_oPX是經度oPY是緯度_並用反查那組金鑰()
    {
        var (c, h) = 建立(geoXySwap: false);
        var hit = await c.ReverseAsync(25.0377, 121.5645, CancellationToken.None);

        Assert.AreEqual("臺北市信義區市府路1號", hit!.Address);
        StringAssert.Contains(h.收到的本文!, "oPX=121.56450000");
        StringAssert.Contains(h.收到的本文!, "oPY=25.03770000");
        StringAssert.Contains(h.收到的本文!, "oSRS=EPSG%3A4326");
        // 反查一定要用 GEO 那組金鑰（與正查的 TgosAppId/TgosApiKey 是不同服務，本例正查那組根本沒設）
        StringAssert.Contains(h.收到的本文!, "oAPPId=app");
        StringAssert.Contains(h.收到的本文!, "oAPIKey=key");
        Assert.AreEqual(RelayConfig.預設TgosGeoUrl, h.收到的Url!.ToString());
    }

    [TestMethod]
    public async Task XY_SWAP開_送出與解析同時翻轉()
    {
        var (c, h) = 建立(geoXySwap: true);
        var hit = await c.ReverseAsync(25.0377, 121.5645, CancellationToken.None);

        StringAssert.Contains(h.收到的本文!, "oPX=25.03770000");
        StringAssert.Contains(h.收到的本文!, "oPY=121.56450000");
        // 回應的 X 也要當成緯度解
        Assert.AreEqual(25.0377, hit!.Lon!.Value, 1e-6);
        Assert.AreEqual(121.5645, hit.Lat!.Value, 1e-6);
    }

    [TestMethod]
    public async Task 座標不受地區設定影響()
    {
        // de-DE 的小數點是逗號：沒有 InvariantCulture 的話會送出 oPX=121,56450000，
        // TGOS 不會回錯誤，只會查無資料——最難查的那種。
        var 原 = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var (c, h) = 建立(geoXySwap: false);
            await c.ReverseAsync(25.0377, 121.5645, CancellationToken.None);
            StringAssert.Contains(h.收到的本文!, "oPX=121.56450000");
            Assert.IsFalse(h.收到的本文!.Contains("121%2C56"), "小數點被寫成逗號：" + h.收到的本文);
        }
        finally { Thread.CurrentThread.CurrentCulture = 原; }
    }

    [TestMethod]
    public async Task 上游回非JSON純文字_是上游故障()
    {
        var h = new 攔截Handler("<string xmlns=\"http://tempuri.org/\">Length of the data to decrypt is invalid.</string>");
        var c = new TgosClient(new HttpClient(h), new RelayConfig { TgosGeoAppId = "a", TgosGeoApiKey = "b" });
        await Assert.ThrowsExceptionAsync<TgosUpstreamException>(
            () => c.ReverseAsync(25.0, 121.5, CancellationToken.None));
    }
}

/// <summary>轉發器設定：反查那組金鑰與正查完全分離。</summary>
[TestClass]
public class RelayConfigGeoTests
{
    private static readonly string[] 相關鍵 =
    [
        "TGOS_APPID", "TGOS_APIKEY", "TGOS_GEO_APPID", "TGOS_GEO_APIKEY", "TGOS_GEO_URL", "TGOS_GEO_XY_SWAP",
    ];

    [TestCleanup]
    public void 清環境()
    {
        foreach (var k in 相關鍵) Environment.SetEnvironmentVariable(k, null);
    }

    [TestMethod]
    public void 缺GEO金鑰時_Geo金鑰已設為false_且走預設端點()
    {
        foreach (var k in 相關鍵) Environment.SetEnvironmentVariable(k, null);
        var c = RelayConfig.FromEnvironment();
        Assert.IsFalse(c.Geo金鑰已設);
        Assert.IsFalse(c.GeoXySwap);
        Assert.AreEqual(RelayConfig.預設TgosGeoUrl, c.TgosGeoUrl);
    }

    [TestMethod]
    public void 正查金鑰有設_不會讓反查誤判成已設定()
    {
        // 兩支是各自申請的服務：正查核准了不代表反查核准了。
        // 若共用旗標，/reverse 會在沒有金鑰的情況下真的打出去，換來一句看不懂的解密錯誤。
        Environment.SetEnvironmentVariable("TGOS_APPID", "a");
        Environment.SetEnvironmentVariable("TGOS_APIKEY", "b");
        var c = RelayConfig.FromEnvironment();
        Assert.IsTrue(c.Tgos金鑰已設);
        Assert.IsFalse(c.Geo金鑰已設);
    }

    [TestMethod]
    public void GEO環境變數有值時讀進來()
    {
        Environment.SetEnvironmentVariable("TGOS_GEO_APPID", " ga ");
        Environment.SetEnvironmentVariable("TGOS_GEO_APIKEY", "gk");
        Environment.SetEnvironmentVariable("TGOS_GEO_URL", "https://example.test/PointQueryNearAddr");
        Environment.SetEnvironmentVariable("TGOS_GEO_XY_SWAP", "TRUE");
        var c = RelayConfig.FromEnvironment();
        Assert.AreEqual("ga", c.TgosGeoAppId);
        Assert.IsTrue(c.Geo金鑰已設);
        Assert.IsTrue(c.GeoXySwap);
        Assert.AreEqual("https://example.test/PointQueryNearAddr", c.TgosGeoUrl);
        Assert.IsFalse(c.XySwap, "反查的旗標不可以污染正查");
    }

    [TestMethod]
    public void 正查端點升到v40()
        => StringAssert.Contains(RelayConfig.預設TgosUrl, "/v40/QueryAddr.asmx/QueryAddr");
}

/// <summary>YYAPI 端反查後端：只測純函式（狀態碼對映、回應解析、精度判定、工廠選型），不連網不連 DB。</summary>
[TestClass]
public class TgosReverseRelayBackendTests
{
    [TestMethod]
    public void found_true_回門牌命中()
    {
        var hit = TgosReverseRelayBackend.解析回應(HttpStatusCode.OK,
            """{"found":true,"address":"臺北市信義區市府路1號","lon":121.5645,"lat":25.0377,"matchType":"完全比對"}""");
        Assert.IsNotNull(hit);
        Assert.AreEqual("臺北市信義區市府路1號", hit!.Address);
        Assert.AreEqual("TGOS", hit.Source);
        Assert.AreEqual(GeoPrecision.門牌, hit.Precision);
    }

    [TestMethod]
    public void 地址沒有號_降成路名精度()
    {
        var hit = TgosReverseRelayBackend.解析回應(HttpStatusCode.OK,
            """{"found":true,"address":"臺北市信義區市府路"}""");
        Assert.AreEqual(GeoPrecision.路名, hit!.Precision);
    }

    [TestMethod]
    public void found_false_回null()
        => Assert.IsNull(TgosReverseRelayBackend.解析回應(HttpStatusCode.OK, """{"found":false}"""));

    [TestMethod]
    public void found_true_缺地址_是上游故障()
    {
        Assert.ThrowsException<GeocodeBackendException>(() =>
            TgosReverseRelayBackend.解析回應(HttpStatusCode.OK, """{"found":true,"lon":121.5,"lat":25.0}"""));
    }

    [TestMethod]
    public void 轉發器401與503_是設定錯不是暫時故障()
    {
        var ex401 = Assert.ThrowsException<GeocodeConfigurationException>(() =>
            TgosReverseRelayBackend.解析回應(HttpStatusCode.Unauthorized, """{"error":"X-Relay-Key 錯誤或缺少"}"""));
        StringAssert.Contains(ex401.Message, TgosReverseRelayBackend.設定鍵_Key);

        var ex503 = Assert.ThrowsException<GeocodeConfigurationException>(() =>
            TgosReverseRelayBackend.解析回應(HttpStatusCode.ServiceUnavailable,
                """{"error":"轉發器未設定 TGOS_GEO_APPID／TGOS_GEO_APIKEY"}"""));
        StringAssert.Contains(ex503.Message, "TGOS_GEO_APPID");
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadRequest)]
    public void 其他狀態碼_是上游故障(HttpStatusCode code)
    {
        Assert.ThrowsException<GeocodeBackendException>(() =>
            TgosReverseRelayBackend.解析回應(code, """{"error":"x"}"""));
    }

    [TestMethod]
    public void 回應不是JSON_是上游故障()
    {
        Assert.ThrowsException<GeocodeBackendException>(() =>
            TgosReverseRelayBackend.解析回應(HttpStatusCode.OK, "<html>gateway</html>"));
    }

    [TestMethod]
    public void 缺設定_建構子丟設定例外並指名鍵()
    {
        var ex = Assert.ThrowsException<GeocodeConfigurationException>(() => new TgosReverseRelayBackend("", ""));
        StringAssert.Contains(ex.Message, TgosReverseRelayBackend.設定鍵_Url);
        StringAssert.Contains(ex.Message, TgosReverseRelayBackend.設定鍵_Key);
        StringAssert.Contains(ex.Message, ReverseGeocodeBackendFactory.設定鍵);
    }

    [TestMethod]
    public void 工廠_TGOS不再是NotSupported()
    {
        Environment.SetEnvironmentVariable(TgosReverseRelayBackend.設定鍵_Url, null);
        Environment.SetEnvironmentVariable(TgosReverseRelayBackend.設定鍵_Key, null);
        // 沒設 URL/KEY → 設定例外（而不是舊的 NotSupportedException「待 /reverse 上線」）
        Assert.ThrowsException<GeocodeConfigurationException>(() => ReverseGeocodeBackendFactory.建立("TGOS"));

        Environment.SetEnvironmentVariable(TgosReverseRelayBackend.設定鍵_Url, "https://tgos-relay.yanyue.io/");
        Environment.SetEnvironmentVariable(TgosReverseRelayBackend.設定鍵_Key, "k");
        try
        {
            var b = ReverseGeocodeBackendFactory.建立("tgos");
            Assert.IsInstanceOfType(b, typeof(TgosReverseRelayBackend));
            Assert.AreEqual("TGOS", b.來源);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TgosReverseRelayBackend.設定鍵_Url, null);
            Environment.SetEnvironmentVariable(TgosReverseRelayBackend.設定鍵_Key, null);
        }
    }
}
