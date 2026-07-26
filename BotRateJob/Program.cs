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
    var scraper = new BankTaiwanRateScraper(logger);

    IReadOnlyList<BankTaiwanSpotRate> rates;
    string summary;

    if (Config.BackfillFrom is DateTime from)
    {
        var to = Config.BackfillTo ?? DateTime.UtcNow.AddHours(8).Date;
        logger.LogInformation("回填模式：{From:yyyy/MM/dd} ~ {To:yyyy/MM/dd}", from, to);

        rates = await scraper.ScrapeRangeAsync(from, to);
        summary = $"匯率回填完成（{from:yyyy/MM/dd} ~ {to:yyyy/MM/dd}，{rates.Count} 筆）";
    }
    else
    {
        var result = await scraper.ScrapeAsync();
        rates = result.Rates;
        summary = $"{result.QuoteTime:yyyy/MM/dd HH:mm} 匯率更新成功（{rates.Count} 筆，來源 {result.Source}）";
        logger.LogInformation("取得 {Count} 筆匯率（來源：{Source}）", rates.Count, result.Source);
    }

    if (rates.Count == 0)
    {
        throw new InvalidOperationException("解析後沒有任何匯率資料");
    }

    if (Config.DryRun)
    {
        logger.LogInformation("DRY_RUN，不寫入 Cosmos：");
        foreach (var rate in rates)
        {
            logger.LogInformation("  {Date} {Currency} 即期買入 {Buying} / 賣出 {Selling}",
                rate.Date, rate.Currency, rate.SpotRateBuying, rate.SpotRateSelling);
        }
        return 0;
    }

    await new CosmosWriter(logger).WriteAsync(rates);
    await notifier.SendAsync(summary);
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
