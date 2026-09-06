using System.Net;
using TS.API.ExtData.Geocode;

namespace TgosRelay.Tests;

/// <summary>YYAPI 端 TGOS 後端：只測純函式（狀態碼對映、回應解析、精度判定、快取 TTL 政策），不連網不連 DB。</summary>
[TestClass]
public class TgosRelayBackendTests
{
    [TestMethod]
    public void found_true_回門牌命中()
    {
        var hit = TgosRelayBackend.解析回應(HttpStatusCode.OK,
            """{"found":true,"lon":121.5645,"lat":25.0377,"matchType":"完全比對","tgosAddress":"臺北市信義區市府路1號"}""");
        Assert.IsNotNull(hit);
        Assert.AreEqual(121.5645, hit!.Lon, 1e-6);
        Assert.AreEqual(25.0377, hit.Lat, 1e-6);
        Assert.AreEqual("TGOS", hit.Source);
        Assert.AreEqual(GeoPrecision.門牌, hit.Precision);
    }

    [TestMethod]
    public void 比對地址沒有號_降成路名精度()
    {
        var hit = TgosRelayBackend.解析回應(HttpStatusCode.OK,
            """{"found":true,"lon":121.5,"lat":25.0,"tgosAddress":"臺北市信義區市府路"}""");
        Assert.AreEqual(GeoPrecision.路名, hit!.Precision);
    }

    [TestMethod]
    public void 沒有比對地址欄位_寧可低報()
    {
        var hit = TgosRelayBackend.解析回應(HttpStatusCode.OK, """{"found":true,"lon":121.5,"lat":25.0}""");
        Assert.AreEqual(GeoPrecision.路名, hit!.Precision);
    }

    [TestMethod]
    public void found_false_回null()
    {
        Assert.IsNull(TgosRelayBackend.解析回應(HttpStatusCode.OK, """{"found":false}"""));
    }

    [TestMethod]
    public void found_true_缺座標_是上游故障()
    {
        Assert.ThrowsException<GeocodeBackendException>(() =>
            TgosRelayBackend.解析回應(HttpStatusCode.OK, """{"found":true,"lon":121.5}"""));
    }

    [TestMethod]
    public void 轉發器401_是設定錯不是暫時故障()
    {
        var ex = Assert.ThrowsException<GeocodeConfigurationException>(() =>
            TgosRelayBackend.解析回應(HttpStatusCode.Unauthorized, """{"error":"X-Relay-Key 錯誤或缺少"}"""));
        StringAssert.Contains(ex.Message, TgosRelayBackend.設定鍵_Key);
    }

    [TestMethod]
    public void 轉發器503_是設定錯()
    {
        var ex = Assert.ThrowsException<GeocodeConfigurationException>(() =>
            TgosRelayBackend.解析回應(HttpStatusCode.ServiceUnavailable, """{"error":"轉發器未設定 TGOS_APPID／TGOS_APIKEY"}"""));
        StringAssert.Contains(ex.Message, "TGOS_APPID");
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadRequest)]
    public void 其他狀態碼_是上游故障(HttpStatusCode code)
    {
        Assert.ThrowsException<GeocodeBackendException>(() =>
            TgosRelayBackend.解析回應(code, """{"error":"x"}"""));
    }

    [TestMethod]
    public void 回應不是JSON_是上游故障()
    {
        Assert.ThrowsException<GeocodeBackendException>(() =>
            TgosRelayBackend.解析回應(HttpStatusCode.OK, "<html>gateway</html>"));
    }

    [TestMethod]
    public void 缺設定_建構子丟設定例外並指名鍵()
    {
        var ex = Assert.ThrowsException<GeocodeConfigurationException>(() => new TgosRelayBackend("", ""));
        StringAssert.Contains(ex.Message, TgosRelayBackend.設定鍵_Url);
        StringAssert.Contains(ex.Message, TgosRelayBackend.設定鍵_Key);
    }

    [TestMethod]
    public void 工廠_TGOS不再是NotSupported()
    {
        // 沒設 URL/KEY → 設定例外（而不是舊的 NotSupportedException「尚未實作」）
        Environment.SetEnvironmentVariable(TgosRelayBackend.設定鍵_Url, null);
        Environment.SetEnvironmentVariable(TgosRelayBackend.設定鍵_Key, null);
        Assert.ThrowsException<GeocodeConfigurationException>(() => GeocodeBackendFactory.建立("TGOS"));

        Environment.SetEnvironmentVariable(TgosRelayBackend.設定鍵_Url, "https://tgos-relay.yanyue.io/");
        Environment.SetEnvironmentVariable(TgosRelayBackend.設定鍵_Key, "k");
        try
        {
            var b = GeocodeBackendFactory.建立("tgos");
            Assert.IsInstanceOfType(b, typeof(TgosRelayBackend));
            Assert.AreEqual("TGOS", b.來源);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TgosRelayBackend.設定鍵_Url, null);
            Environment.SetEnvironmentVariable(TgosRelayBackend.設定鍵_Key, null);
        }
    }
}

[TestClass]
public class GeocodeCacheTtlPolicyTests
{
    [TestCleanup]
    public void 清環境() => Environment.SetEnvironmentVariable(GeocodeCache.設定鍵_Tgos保留時數, null);

    [TestMethod]
    public void 免費來源_不到期_可寫()
    {
        Assert.IsNull(GeocodeCache.找到保留時數("NOMINATIM"));
        Assert.IsNull(GeocodeCache.找到保留時數(null));
        Assert.IsTrue(GeocodeCache.可寫快取("NOMINATIM"));
    }

    [TestMethod]
    public void TGOS_預設24小時()
    {
        Environment.SetEnvironmentVariable(GeocodeCache.設定鍵_Tgos保留時數, null);
        Assert.AreEqual(TimeSpan.FromHours(24), GeocodeCache.找到保留時數("TGOS"));
        Assert.AreEqual(TimeSpan.FromHours(24), GeocodeCache.找到保留時數(" tgos "));
        Assert.IsTrue(GeocodeCache.可寫快取("TGOS"));
    }

    [TestMethod]
    public void TGOS_設0_不寫不讀()
    {
        Environment.SetEnvironmentVariable(GeocodeCache.設定鍵_Tgos保留時數, "0");
        Assert.AreEqual(TimeSpan.Zero, GeocodeCache.找到保留時數("TGOS"));
        Assert.IsFalse(GeocodeCache.可寫快取("TGOS"));
    }

    [TestMethod]
    public void TGOS_設定值無效_退回24()
    {
        Environment.SetEnvironmentVariable(GeocodeCache.設定鍵_Tgos保留時數, "abc");
        Assert.AreEqual(TimeSpan.FromHours(24), GeocodeCache.找到保留時數("TGOS"));
        Environment.SetEnvironmentVariable(GeocodeCache.設定鍵_Tgos保留時數, "-5");
        Assert.AreEqual(TimeSpan.FromHours(24), GeocodeCache.找到保留時數("TGOS"));
    }
}
