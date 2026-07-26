using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace BotRateJob;

public sealed record ScrapeResult(DateTime QuoteTime, IReadOnlyList<BankTaiwanSpotRate> Rates, string Source);

/// <summary>
/// 用真實瀏覽器引擎取得台銀牌告匯率。
/// 台銀已在 CDN 層加上需執行 JavaScript 的防爬蟲挑戰，純 HttpClient 只會拿到 HTTP 200 的挑戰頁，
/// 因此改用 Playwright 開啟頁面、等挑戰自行通過後再取資料。
/// </summary>
public sealed class BankTaiwanRateScraper
{
    private const string RatePageUrl = "https://rate.bot.com.tw/xrt?Lang=zh-TW";
    private const string CsvPath = "/xrt/flcsv/0/day";
    private const string RateRowSelector = "table[title='牌告匯率'] tbody tr";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // 挑戰頁自稱需要約 30 秒，留兩倍餘裕
    private const int ChallengeTimeoutMs = 60_000;

    private readonly ILogger _logger;

    public BankTaiwanRateScraper(ILogger logger) => _logger = logger;

    /// <summary>
    /// 開瀏覽器、通過防爬蟲挑戰，然後把已通過挑戰的頁面交給 action 使用。
    /// </summary>
    private async Task<T> WithChallengedPageAsync<T>(Func<IPage, Task<T>> action)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" }
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = BrowserUserAgent,
            Locale = "zh-TW",
            TimezoneId = "Asia/Taipei",
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });

        var page = await context.NewPageAsync();

        _logger.LogInformation("開啟 {Url}", RatePageUrl);
        await page.GotoAsync(RatePageUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = ChallengeTimeoutMs
        });

        // 挑戰頁會自己跑完 JS 再重新載入，等真正的匯率表格出現代表已通過
        await page.WaitForSelectorAsync(RateRowSelector, new PageWaitForSelectorOptions { Timeout = ChallengeTimeoutMs });
        _logger.LogInformation("已通過防爬蟲挑戰");

        return await action(page);
    }

    public Task<ScrapeResult> ScrapeAsync() => WithChallengedPageAsync(ScrapeLatestAsync);

    private async Task<ScrapeResult> ScrapeLatestAsync(IPage page)
    {
        var quoteTime = await ReadQuoteTimeAsync(page);
        _logger.LogInformation("牌價掛牌時間：{QuoteTime:yyyy/MM/dd HH:mm}", quoteTime);

        // 主要路徑：沿用已通過挑戰的 session，直接抓官方 CSV（與台銀網頁「下載 Excel (CSV) 檔」同一支）
        if (Config.ForceTableParse)
        {
            _logger.LogInformation("FORCE_TABLE_PARSE 已設定，直接使用表格解析");
        }
        else
        {
            try
            {
                var csv = await FetchCsvAsync(page);
                var rates = ParseCsv(csv, quoteTime);
                if (rates.Count > 0)
                {
                    return new ScrapeResult(quoteTime, rates, "CSV");
                }
                _logger.LogWarning("CSV 解析結果為空，改用網頁表格解析");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CSV 取得或解析失敗，改用網頁表格解析");
            }
        }

        // 備援路徑：直接讀畫面上的表格
        var fromTable = await ParseTableAsync(page, quoteTime);
        return new ScrapeResult(quoteTime, fromTable, "HTML 表格");
    }

    /// <summary>
    /// 回填指定日期區間。通過一次挑戰後沿用同一個 session 逐日抓取，
    /// 沒有資料的日期（假日、台銀未保留的久遠日期）會跳過。
    /// </summary>
    public Task<List<BankTaiwanSpotRate>> ScrapeRangeAsync(DateTime from, DateTime to) =>
        WithChallengedPageAsync(page => ScrapeRangeCoreAsync(page, from, to));

    private async Task<List<BankTaiwanSpotRate>> ScrapeRangeCoreAsync(IPage page, DateTime from, DateTime to)
    {
        var all = new List<BankTaiwanSpotRate>();
        var skipped = new List<DateTime>();

        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            string csv;
            try
            {
                csv = await FetchCsvAsync(page, $"/xrt/flcsv/0/{date:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{Date:yyyy/MM/dd} 取得失敗，跳過：{Message}", date, ex.Message);
                skipped.Add(date);
                continue;
            }

            var rates = ParseCsv(csv, date);
            if (rates.Count == 0)
            {
                skipped.Add(date);
                continue;
            }

            all.AddRange(rates);
            _logger.LogInformation("{Date:yyyy/MM/dd} 取得 {Count} 筆", date, rates.Count);

            // 逐日連續請求，稍作間隔避免觸發流量限制
            await Task.Delay(1500);
        }

        _logger.LogInformation("回填完成：{Days} 天有資料、{Skipped} 天無資料，共 {Total} 筆",
            all.Select(r => r.Date).Distinct().Count(), skipped.Count, all.Count);

        if (skipped.Count > 0)
        {
            _logger.LogInformation("無資料的日期：{Dates}",
                string.Join(", ", skipped.Select(d => d.ToString("MM/dd"))));
        }

        return all;
    }

    /// <summary>讀取「牌價最新掛牌時間：2026/07/25 09:25」。讀不到就退回台北時間現在。</summary>
    private async Task<DateTime> ReadQuoteTimeAsync(IPage page)
    {
        var text = await page.EvaluateAsync<string?>(
            """
            () => {
                const el = [...document.querySelectorAll('p, span, div')]
                    .find(e => e.textContent.includes('牌價最新掛牌時間'));
                return el ? el.textContent.trim() : null;
            }
            """);

        if (text is not null)
        {
            var m = Regex.Match(text, @"(\d{4}/\d{1,2}/\d{1,2})\s+(\d{1,2}:\d{2})");
            if (m.Success && DateTime.TryParseExact(
                    $"{m.Groups[1].Value} {m.Groups[2].Value}",
                    new[] { "yyyy/M/d H:mm", "yyyy/MM/dd HH:mm" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                return parsed;
            }
            _logger.LogWarning("掛牌時間格式無法解析：{Text}", text);
        }
        else
        {
            _logger.LogWarning("找不到掛牌時間，改用目前台北時間");
        }

        return DateTime.UtcNow.AddHours(8);
    }

    private async Task<string> FetchCsvAsync(IPage page, string? path = null)
    {
        var csv = await page.EvaluateAsync<string>(
            """
            async (path) => {
                const res = await fetch(path, { credentials: 'include' });
                if (!res.ok) throw new Error('HTTP ' + res.status);
                return await res.text();
            }
            """, path ?? CsvPath);

        if (string.IsNullOrWhiteSpace(csv) || !csv.TrimStart().StartsWith("幣別"))
        {
            var head = csv is null ? "(null)" : csv[..Math.Min(120, csv.Length)];
            throw new InvalidOperationException($"取得的內容不是預期的 CSV：{head}");
        }

        return csv;
    }

    private List<BankTaiwanSpotRate> ParseCsv(string csvData, DateTime quoteTime)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // 每列結尾多一個逗號，且缺值以 0.00000 表示，這兩項避免因此中斷
            BadDataFound = null,
            MissingFieldFound = null
        };

        using var reader = new StringReader(csvData);
        using var csv = new CsvReader(reader, config);
        csv.Context.RegisterClassMap<FXRateCsvPocoMap>();

        var list = new List<BankTaiwanSpotRate>();
        foreach (var item in csv.GetRecords<FXRateCsvPoco>())
        {
            if (string.IsNullOrWhiteSpace(item.幣別)) continue;
            list.Add(Create(quoteTime, item.幣別.Trim(), item.即期, item.即期1));
        }

        _logger.LogInformation("CSV 解析到 {Count} 筆", list.Count);
        return list;
    }

    private async Task<List<BankTaiwanSpotRate>> ParseTableAsync(IPage page, DateTime quoteTime)
    {
        // 回傳 [[幣別, 即期買入, 即期賣出], ...]
        // 注意：同一列有給列印用的重複欄位，querySelector 取到的第一個才是畫面上的值
        var rows = await page.EvaluateAsync<string[][]>(
            """
            () => [...document.querySelectorAll('table[title="牌告匯率"] tbody tr')].map(tr => {
                const pick = sel => {
                    const el = tr.querySelector(sel);
                    return el ? el.innerText.trim() : '';
                };
                const currency = pick('td[data-table="幣別"] div.hidden-phone') || pick('td[data-table="幣別"]');
                return [currency, pick('td[data-table="本行即期買入"]'), pick('td[data-table="本行即期賣出"]')];
            })
            """);

        var list = new List<BankTaiwanSpotRate>();
        foreach (var row in rows)
        {
            var code = ExtractCurrencyCode(row[0]);
            if (code is null)
            {
                _logger.LogWarning("無法取得幣別代碼：{Raw}", row[0]);
                continue;
            }
            list.Add(Create(quoteTime, code, ParseRate(row[1]), ParseRate(row[2])));
        }

        _logger.LogInformation("表格解析到 {Count} 筆", list.Count);
        return list;
    }

    /// <summary>「美金 (USD)」→「USD」。</summary>
    private static string? ExtractCurrencyCode(string text)
    {
        var m = Regex.Match(text, @"\(([A-Za-z]{3})\)");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>表格中沒有該匯率時顯示「-」，統一轉成 0，與 CSV 的 0.00000 一致。</summary>
    private static decimal ParseRate(string text)
    {
        var cleaned = text.Replace(",", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    private static BankTaiwanSpotRate Create(DateTime quoteTime, string currency, decimal buying, decimal selling) => new()
    {
        Date = quoteTime.ToString("yyyy/MM/dd"),
        Currency = currency,
        SpotRateBuying = buying,
        SpotRateSelling = selling,
        // 同一天同一幣別用固定 id，重跑會覆蓋而不是長出重複資料
        id = $"{quoteTime:yyyyMMdd}-{currency}"
    };
}
