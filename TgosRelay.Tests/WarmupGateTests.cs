using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TS.API.ExtData;

namespace TgosRelay.Tests;

/// <summary>
/// 暖機端點的預熱閘（2026-10-02）：單飛、成功後不再執行、失敗冷卻、只重試失敗段、
/// 預熱本身丟例外時冷卻仍生效。段與時鐘都注入假的，不連 DB、不等真時間。
/// </summary>
[TestClass]
public class WarmupGateTests
{
    private static readonly TimeSpan 冷卻 = TimeSpan.FromSeconds(30);

    /// <summary>可手動推進的時鐘。</summary>
    private sealed class 假時鐘
    {
        public DateTime 現在 { get; set; } = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>每次寫 log 都丟例外的 logger——模擬「預熱方法本身丟例外」。</summary>
    private sealed class 會丟例外的Logger : ILogger
    {
        public int 次數;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref 次數);
            throw new InvalidOperationException("logger 故障");
        }
    }

    [TestMethod]
    public async Task 並行呼叫只執行一次_成功後不再執行()
    {
        var 次數 = 0;
        var 放行 = new TaskCompletionSource();
        var 閘 = new 預熱閘(new (string, Func<Task>)[]
        {
            ("DB", async () => { Interlocked.Increment(ref 次數); await 放行.Task; }),
        }, 冷卻);

        var 呼叫們 = Enumerable.Range(0, 50)
            .Select(_ => Task.Factory.StartNew(() => 閘.確保預熱(NullLogger.Instance)))
            .ToArray();
        var 預熱們 = await Task.WhenAll(呼叫們);   // 取得各自拿到的預熱 Task（尚未完成）
        放行.SetResult();
        var 結果 = await Task.WhenAll(預熱們);

        Assert.IsTrue(結果.All(x => x));
        Assert.AreEqual(1, 次數, "並行 50 次應只執行 1 次");

        Assert.IsTrue(await 閘.確保預熱(NullLogger.Instance));
        Assert.AreEqual(1, 次數, "成功後再呼叫不應再執行（不碰 DB）");
    }

    [TestMethod]
    public async Task 失敗後冷卻內不重試_冷卻後只重試失敗的段()
    {
        var 鐘 = new 假時鐘();
        int a次數 = 0, b次數 = 0;
        var b該失敗 = true;
        var 閘 = new 預熱閘(new (string, Func<Task>)[]
        {
            ("A", () => { a次數++; return Task.CompletedTask; }),
            ("B", () => { b次數++; return b該失敗 ? throw new Exception("B 掛了") : Task.CompletedTask; }),
        }, 冷卻, () => 鐘.現在);

        Assert.IsFalse(await 閘.確保預熱(NullLogger.Instance));
        Assert.AreEqual((1, 1), (a次數, b次數));

        鐘.現在 += TimeSpan.FromSeconds(29);
        Assert.IsFalse(await 閘.確保預熱(NullLogger.Instance));
        Assert.AreEqual((1, 1), (a次數, b次數), "冷卻 30 秒內不應重試任何段");

        鐘.現在 += TimeSpan.FromSeconds(2);   // 距失敗 31 秒
        Assert.IsFalse(await 閘.確保預熱(NullLogger.Instance));
        Assert.AreEqual((1, 2), (a次數, b次數), "冷卻後只重試失敗的 B，A 不再碰");

        b該失敗 = false;
        鐘.現在 += TimeSpan.FromSeconds(31);
        Assert.IsTrue(await 閘.確保預熱(NullLogger.Instance));
        Assert.AreEqual((1, 3), (a次數, b次數));

        鐘.現在 += TimeSpan.FromHours(1);
        Assert.IsTrue(await 閘.確保預熱(NullLogger.Instance));
        Assert.AreEqual((1, 3), (a次數, b次數), "成功後不再執行");
    }

    [TestMethod]
    public async Task 預熱方法丟例外時_回false不faulted_且冷卻仍生效()
    {
        var 鐘 = new 假時鐘();
        var 段次數 = 0;
        var logger = new 會丟例外的Logger();
        var 閘 = new 預熱閘(new (string, Func<Task>)[]
        {
            ("A", () => { 段次數++; return Task.CompletedTask; }),
        }, 冷卻, () => 鐘.現在);

        var t = 閘.確保預熱(logger);
        Assert.IsFalse(await t, "例外應視同失敗回 false");
        Assert.IsFalse(t.IsFaulted);
        var log次數 = logger.次數;
        Assert.IsTrue(log次數 >= 1, "測試前提：logger 確實丟過例外");

        鐘.現在 += TimeSpan.FromSeconds(10);
        Assert.IsFalse(await 閘.確保預熱(logger));
        Assert.AreEqual(log次數, logger.次數, "冷卻內不應再執行預熱（logger 沒被再呼叫）");

        鐘.現在 += TimeSpan.FromSeconds(25);   // 距失敗 35 秒
        Assert.IsFalse(await 閘.確保預熱(logger));
        Assert.IsTrue(logger.次數 > log次數, "冷卻過後才重試");
        Assert.AreEqual(1, 段次數, "已完成的段 A 不重做");
    }
}
