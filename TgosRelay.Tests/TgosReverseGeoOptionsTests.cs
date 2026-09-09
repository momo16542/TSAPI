using TgosRelay;

namespace TgosRelay.Tests;

/// <summary>
/// 2026-09-09 使用者裁決兩件可調參數：① 反查地址預設不含里／鄰 ② 反查結果超過距離門檻視同查無。
/// 這裡只測純函式：<see cref="TgosReverseParser"/> 的地址口徑、<see cref="TgosReverseGate"/> 的距離判定、
/// <see cref="RelayConfig"/> 的新設定鍵讀取。不連網、不連 DB。
/// </summary>
[TestClass]
public class TgosReverseParserVillageTests
{
    private const string 台北案例 = """
        {"FULL_ADDR":"臺北市信義區三張里34鄰信義路五段100號","COUNTY":"臺北市","TOWN":"信義區",
         "VILLAGE":"三張里","NEIGHBORHOOD":"34鄰","ROAD":"信義路","SECTION":"5","LANE":null,"ALLEY":null,
         "SUB_ALLEY":null,"TONG":null,"AREA":null,"NUMBER":"100號","X":121.565642,"Y":25.032751,"DIS":70.4121360861}
        """;

    private const string 高雄案例 = """
        {"FULL_ADDR":"高雄市新興區榮治里5鄰民生二路40號","VILLAGE":"榮治里","NEIGHBORHOOD":"5鄰",
         "ROAD":"民生二路","NUMBER":"40號","X":120.30,"Y":22.62}
        """;

    [TestMethod]
    public void 台北案例_預設去掉里鄰()
    {
        var hit = TgosReverseParser.Parse(台北案例);
        Assert.AreEqual("臺北市信義區信義路五段100號", hit!.Address);
    }

    [TestMethod]
    public void 高雄案例_預設去掉里鄰()
    {
        var hit = TgosReverseParser.Parse(高雄案例);
        Assert.AreEqual("高雄市新興區民生二路40號", hit!.Address);
    }

    [TestMethod]
    public void includeVillage為true時_原樣不動()
    {
        var hit台北 = TgosReverseParser.Parse(台北案例, includeVillage: true);
        Assert.AreEqual("臺北市信義區三張里34鄰信義路五段100號", hit台北!.Address);

        var hit高雄 = TgosReverseParser.Parse(高雄案例, includeVillage: true);
        Assert.AreEqual("高雄市新興區榮治里5鄰民生二路40號", hit高雄!.Address);
    }

    [TestMethod]
    public void 顯式includeVillage為false_與預設行為一致()
    {
        var hit = TgosReverseParser.Parse(台北案例, includeVillage: false);
        Assert.AreEqual("臺北市信義區信義路五段100號", hit!.Address);
    }

    [TestMethod]
    public void VILLAGE與NEIGHBORHOOD都是null_不炸不亂刪()
    {
        var hit = TgosReverseParser.Parse(
            """{"FULL_ADDR":"臺北市信義區市府路1號","VILLAGE":null,"NEIGHBORHOOD":null,"X":121.5645,"Y":25.0377}""");
        Assert.AreEqual("臺北市信義區市府路1號", hit!.Address);
    }

    [TestMethod]
    public void VILLAGE與NEIGHBORHOOD都是空字串_不炸不亂刪()
    {
        var hit = TgosReverseParser.Parse(
            """{"FULL_ADDR":"臺北市信義區市府路1號","VILLAGE":"","NEIGHBORHOOD":"","X":121.5645,"Y":25.0377}""");
        Assert.AreEqual("臺北市信義區市府路1號", hit!.Address);
    }

    [TestMethod]
    public void 沒有VILLAGE與NEIGHBORHOOD欄位_不炸不亂刪()
    {
        var hit = TgosReverseParser.Parse("""{"FULL_ADDR":"臺北市信義區市府路1號","X":121.5645,"Y":25.0377}""");
        Assert.AreEqual("臺北市信義區市府路1號", hit!.Address);
    }

    [TestMethod]
    public void 只有VILLAGE沒有NEIGHBORHOOD_只刪里()
    {
        var hit = TgosReverseParser.Parse(
            """{"FULL_ADDR":"臺北市信義區三張里信義路五段100號","VILLAGE":"三張里","X":121.5,"Y":25.0}""");
        Assert.AreEqual("臺北市信義區信義路五段100號", hit!.Address);
    }
}

/// <summary>DIS 欄的解析：數字／字串／缺欄三種都要吃得下。</summary>
[TestClass]
public class TgosReverseParserDisTests
{
    [TestMethod]
    public void DIS是數字()
    {
        var hit = TgosReverseParser.Parse("""{"FULL_ADDR":"某處","DIS":70.4121360861}""");
        Assert.IsNotNull(hit!.Dis);
        Assert.AreEqual(70.4121360861, hit.Dis!.Value, 1e-6);
    }

    [TestMethod]
    public void DIS是字串()
    {
        var hit = TgosReverseParser.Parse("""{"FULL_ADDR":"某處","DIS":"70.41"}""");
        Assert.IsNotNull(hit!.Dis);
        Assert.AreEqual(70.41, hit.Dis!.Value, 1e-6);
    }

    [TestMethod]
    public void DIS缺欄_是null不是0()
    {
        var hit = TgosReverseParser.Parse("""{"FULL_ADDR":"某處"}""");
        Assert.IsNull(hit!.Dis);
    }
}

/// <summary>距離門檻的純函式判定：199／200／201 三個邊界，以及 0＝不限制。</summary>
[TestClass]
public class TgosReverseGateTests
{
    [TestMethod]
    public void 距離小於門檻_不算太遠()
        => Assert.IsFalse(TgosReverseGate.太遠(199, 200));

    [TestMethod]
    public void 距離等於門檻_不算太遠()
        => Assert.IsFalse(TgosReverseGate.太遠(200, 200));

    [TestMethod]
    public void 距離超過門檻_算太遠()
        => Assert.IsTrue(TgosReverseGate.太遠(201, 200));

    [TestMethod]
    public void 門檻為0_完全不擋()
    {
        Assert.IsFalse(TgosReverseGate.太遠(1, 0));
        Assert.IsFalse(TgosReverseGate.太遠(1_000_000, 0));
        Assert.IsFalse(TgosReverseGate.太遠(null, 0));
    }

    [TestMethod]
    public void 距離缺值_不擋()
        => Assert.IsFalse(TgosReverseGate.太遠(null, 200));

    [TestMethod]
    public void 門檻為負數_視同不限制()
        => Assert.IsFalse(TgosReverseGate.太遠(1_000_000, -1));
}

/// <summary>RelayConfig 的兩個新設定鍵：預設值與環境變數讀取。</summary>
[TestClass]
public class RelayConfigGeoOptionsTests
{
    private static readonly string[] 相關鍵 = ["TGOS_GEO_INCLUDE_VILLAGE", "TGOS_GEO_MAX_DIST_M"];

    [TestCleanup]
    public void 清環境()
    {
        foreach (var k in 相關鍵) Environment.SetEnvironmentVariable(k, null);
    }

    [TestMethod]
    public void 環境變數缺時_預設不含里鄰且門檻200()
    {
        foreach (var k in 相關鍵) Environment.SetEnvironmentVariable(k, null);
        var c = RelayConfig.FromEnvironment();
        Assert.IsFalse(c.GeoIncludeVillage);
        Assert.AreEqual(200, c.GeoMaxDistanceM);
    }

    [TestMethod]
    public void TGOS_GEO_INCLUDE_VILLAGE設true_讀進來()
    {
        Environment.SetEnvironmentVariable("TGOS_GEO_INCLUDE_VILLAGE", "TRUE");
        var c = RelayConfig.FromEnvironment();
        Assert.IsTrue(c.GeoIncludeVillage);
    }

    [TestMethod]
    public void TGOS_GEO_MAX_DIST_M設0_讀進來()
    {
        Environment.SetEnvironmentVariable("TGOS_GEO_MAX_DIST_M", "0");
        var c = RelayConfig.FromEnvironment();
        Assert.AreEqual(0, c.GeoMaxDistanceM);
    }

    [TestMethod]
    public void TGOS_GEO_MAX_DIST_M設非數字_退回預設200()
    {
        Environment.SetEnvironmentVariable("TGOS_GEO_MAX_DIST_M", "abc");
        var c = RelayConfig.FromEnvironment();
        Assert.AreEqual(200, c.GeoMaxDistanceM);
    }

    [TestMethod]
    public void TGOS_GEO_MAX_DIST_M設負數_退回預設200()
    {
        // 負數沒有物理意義（距離不會是負的），退回預設比讓它變成「一律不擋」更安全
        Environment.SetEnvironmentVariable("TGOS_GEO_MAX_DIST_M", "-5");
        var c = RelayConfig.FromEnvironment();
        Assert.AreEqual(200, c.GeoMaxDistanceM);
    }
}
