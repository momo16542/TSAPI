using System.Diagnostics;
using TgosRelay;

namespace TgosRelay.Tests;

[TestClass]
public class TgosResponseParserTests
{
    private const string 罐頭 = """{"AddressList":[{"FULL_ADDR":"臺北市信義區市府路1號","X":121.5645,"Y":25.0377,"MATCH_TYPE":"完全比對"}]}""";

    [TestMethod]
    public void 純JSON_取第一筆XY與比對資訊()
    {
        var hit = TgosResponseParser.Parse(罐頭);
        Assert.IsNotNull(hit);
        Assert.AreEqual(121.5645, hit!.Lon, 1e-6);
        Assert.AreEqual(25.0377, hit.Lat, 1e-6);
        Assert.AreEqual("完全比對", hit.MatchType);
        Assert.AreEqual("臺北市信義區市府路1號", hit.FullAddress);
        Assert.AreEqual("完全比對", hit.Raw.GetProperty("MATCH_TYPE").GetString());
    }

    [TestMethod]
    public void ASMX的XML外殼會先剝掉()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><string xmlns=\"http://tempuri.org/\">"
                  + System.Security.SecurityElement.Escape(罐頭) + "</string>";
        var hit = TgosResponseParser.Parse(xml);
        Assert.IsNotNull(hit);
        Assert.AreEqual(121.5645, hit!.Lon, 1e-6);
    }

    [TestMethod]
    public void XY對調開關()
    {
        var hit = TgosResponseParser.Parse(罐頭, xySwap: true);
        Assert.AreEqual(25.0377, hit!.Lon, 1e-6);
        Assert.AreEqual(121.5645, hit.Lat, 1e-6);
    }

    [TestMethod]
    public void 字串型XY也能解()
    {
        var hit = TgosResponseParser.Parse("""[{"X":"121.5","Y":"25.0"}]""");
        Assert.AreEqual(121.5, hit!.Lon, 1e-9);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("[]")]
    [DataRow("""{"AddressList":[]}""")]
    [DataRow("""{"Message":"no X here"}""")]
    [DataRow("<string xmlns=\"x\">[]</string>")]
    public void 查無回null(string body)
    {
        Assert.IsNull(TgosResponseParser.Parse(body));
    }
}

[TestClass]
public class ThrottleTests
{
    [TestMethod]
    public async Task 兩次連續呼叫至少隔最小間隔()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        await t.RunAsync(_ => Task.FromResult(1), CancellationToken.None);
        await t.RunAsync(_ => Task.FromResult(2), CancellationToken.None);
        Assert.IsTrue(sw.ElapsedMilliseconds >= 280, $"實際 {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task 並行三個請求會排成一列()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(200));
        var 時間點 = new List<long>();
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 3).Select(_ => t.RunAsync(_ =>
        {
            lock (時間點) 時間點.Add(sw.ElapsedMilliseconds);
            return Task.FromResult(0);
        }, CancellationToken.None));
        await Task.WhenAll(tasks);
        時間點.Sort();
        Assert.IsTrue(時間點[1] - 時間點[0] >= 180 && 時間點[2] - 時間點[1] >= 180, string.Join(",", 時間點));
    }
}

[TestClass]
public class RelayConfigTests
{
    [TestMethod]
    public void 環境變數缺時走預設()
    {
        foreach (var k in new[] { "RELAY_KEY", "TGOS_APPID", "TGOS_APIKEY", "TGOS_URL", "TGOS_XY_SWAP", "TGOS_MIN_INTERVAL_MS" })
            Environment.SetEnvironmentVariable(k, null);
        var c = RelayConfig.FromEnvironment();
        Assert.IsFalse(c.RelayKey已設);
        Assert.IsFalse(c.Tgos金鑰已設);
        Assert.AreEqual(RelayConfig.預設TgosUrl, c.TgosUrl);
        Assert.IsFalse(c.XySwap);
        Assert.AreEqual(1000, c.MinIntervalMs);
    }

    [TestMethod]
    public void 環境變數有值時讀進來()
    {
        Environment.SetEnvironmentVariable("RELAY_KEY", " k ");
        Environment.SetEnvironmentVariable("TGOS_APPID", "a");
        Environment.SetEnvironmentVariable("TGOS_APIKEY", "b");
        Environment.SetEnvironmentVariable("TGOS_XY_SWAP", "TRUE");
        Environment.SetEnvironmentVariable("TGOS_MIN_INTERVAL_MS", "250");
        try
        {
            var c = RelayConfig.FromEnvironment();
            Assert.AreEqual("k", c.RelayKey);
            Assert.IsTrue(c.Tgos金鑰已設);
            Assert.IsTrue(c.XySwap);
            Assert.AreEqual(250, c.MinIntervalMs);
        }
        finally
        {
            foreach (var k in new[] { "RELAY_KEY", "TGOS_APPID", "TGOS_APIKEY", "TGOS_XY_SWAP", "TGOS_MIN_INTERVAL_MS" })
                Environment.SetEnvironmentVariable(k, null);
        }
    }
}
