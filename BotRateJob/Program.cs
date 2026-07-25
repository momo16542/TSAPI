using BotRateJob;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger("BotRateJob");
var notifier = new TelegramNotify(logger);

try
{
    var result = await new BankTaiwanRateScraper(logger).ScrapeAsync();

    if (result.Rates.Count == 0)
    {
        throw new InvalidOperationException("解析後沒有任何匯率資料");
    }

    logger.LogInformation("取得 {Count} 筆匯率（來源：{Source}）", result.Rates.Count, result.Source);

    if (Config.DryRun)
    {
        logger.LogInformation("DRY_RUN，不寫入 Cosmos：");
        foreach (var rate in result.Rates)
        {
            logger.LogInformation("  {Date} {Currency} 即期買入 {Buying} / 賣出 {Selling}",
                rate.Date, rate.Currency, rate.SpotRateBuying, rate.SpotRateSelling);
        }
        return 0;
    }

    await new CosmosWriter(logger).WriteAsync(result.Rates);
    await notifier.SendAsync(
        $"{result.QuoteTime:yyyy/MM/dd HH:mm} 匯率更新成功（{result.Rates.Count} 筆，來源 {result.Source}）");
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "匯率更新失敗");
    if (!Config.DryRun)
    {
        await notifier.SendAsync($"匯率更新失敗：{ex.GetType().Name} {ex.Message}");
    }
    // 非 0 結束碼會讓 Container Apps Job 標記為失敗
    return 1;
}
