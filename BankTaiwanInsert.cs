using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.CosmosDB;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using TSAPI;

namespace TS.TimeTrigger
{
    public class BankTaiwanInsert
    {
        private readonly ILogger _logger;

        public BankTaiwanInsert(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<BankTaiwanInsert>();
        }

        [Function("BankTaiwanInsert")]
        public async Task<MultiResponse> Run([TimerTrigger("0 0 10 * * *")] MyInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
            _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus?.Next}");
            try
            {
                var list = await DownloadAndParseExchangeRates();
                _logger.LogInformation($"fx rate completed");
                return new MultiResponse() { Document = list };
            }
            catch (Exception ex)
            {
                // 保底：任何未預期的錯誤（逾時、CSV 解析失敗等）都發 Telegram 通知
                _logger.LogError(ex, "BankTaiwanInsert failed unexpectedly");
                await TelegramNotify.SendNotify($"匯率更新失敗（未預期錯誤）：{ex.GetType().Name} {ex.Message}", _logger, "2");
                return new MultiResponse() { Document = new List<BankTaiwanSpotRate>() };
            }
        }
        private async Task<List<BankTaiwanSpotRate>> DownloadAndParseExchangeRates()
        {
            List<BankTaiwanSpotRate> list = new List<BankTaiwanSpotRate>();
            const string NoDataMessage = "很抱歉，本次查詢找不到任何一筆資料！";
            const int MaxLookbackDays = 30;

            DateTime targetDate = DateTime.UtcNow.AddHours(8).Date;
            DateTime dataDate = targetDate;
            string csvData = null;

            // 保留 cookie，讓防爬蟲挑戰通過後的 cookie 能沿用到後續請求
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = new CookieContainer(),
                UseCookies = true
            };

            using (var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            {
                // 模擬瀏覽器請求，避免被台銀網站的防爬蟲機制擋下
                httpClient.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
                httpClient.DefaultRequestHeaders.Add("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-TW,zh;q=0.9,en;q=0.8");
                httpClient.DefaultRequestHeaders.Add("Referer", "https://rate.bot.com.tw/xrt?Lang=zh-TW");
                httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
                httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
                httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
                httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");

                // 先打一次首頁取得 cookie，直接抓 CSV 較容易觸發挑戰頁
                await WarmUpAsync(httpClient);

                for (int i = 0; i < MaxLookbackDays; i++)
                {
                    DateTime tryDate = targetDate.AddDays(-i);
                    string url = $"https://rate.bot.com.tw/xrt/flcsv/0/{tryDate:yyyy-MM-dd}";
                    _logger.LogInformation($"Trying to download exchange rates from: {url}");

                    string content;
                    try
                    {
                        content = await FetchWithRetryAsync(httpClient, url);
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(ex, $"Request blocked or failed for {tryDate:yyyy-MM-dd} (StatusCode: {ex.StatusCode})");
                        await TelegramNotify.SendNotify($"匯率下載被擋 (HTTP {(int?)ex.StatusCode})，更新失敗", _logger, "2");
                        return list;
                    }
                    catch (TaskCanceledException ex)
                    {
                        _logger.LogError(ex, $"Request timed out for {tryDate:yyyy-MM-dd}");
                        await TelegramNotify.SendNotify("匯率下載逾時，更新失敗", _logger, "2");
                        return list;
                    }

                    if (content == null)
                    {
                        // 重試後仍是防爬蟲挑戰頁，繼續往前試也只會被擋，直接結束
                        _logger.LogError("Blocked by anti-bot challenge page, giving up.");
                        await TelegramNotify.SendNotify("匯率下載被防爬蟲攔截（Challenge Validation），更新失敗", _logger, "2");
                        return list;
                    }

                    if (content.Contains(NoDataMessage))
                    {
                        _logger.LogInformation($"No data for {tryDate:yyyy-MM-dd}, trying previous day.");
                        continue;
                    }

                    csvData = content;
                    dataDate = tryDate;
                    break;
                }
            }

            if (csvData == null)
            {
                _logger.LogWarning($"No exchange rate data found within {MaxLookbackDays} days lookback.");
                await TelegramNotify.SendNotify("沒抓到匯率，更新失敗", _logger, "2");
                return list;
            }

            var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                // 保底：真的遇到格式怪異的欄位時略過，不要整批中斷
                BadDataFound = null
            };

            var stringReader = new StringReader(csvData);
            using (var csvReader = new CsvReader(stringReader, csvConfig))
            {
                csvReader.Context.RegisterClassMap<FXRateCsvPocoMap>();
                var records = csvReader.GetRecords<FXRateCsvPoco>();
                foreach (var item in records)
                {
                    list.Add(new BankTaiwanSpotRate()
                    {
                        Date = dataDate.ToString("yyyy/MM/dd"),
                        Currency = item.幣別,
                        SpotRateBuying = item.即期,
                        SpotRateSelling = item.即期1,
                        id = System.Guid.NewGuid().ToString()
                    });

                }
                await TelegramNotify.SendNotify($"{DateTime.UtcNow.AddHours(8)} 匯率更新成功", _logger, "2");
                return list;
            }
        }

        /// <summary>
        /// 先請求一次匯率首頁取得 session cookie，降低直接抓 CSV 被防爬蟲擋下的機率。失敗不影響後續流程。
        /// </summary>
        private async Task WarmUpAsync(HttpClient httpClient)
        {
            try
            {
                await httpClient.GetStringAsync("https://rate.bot.com.tw/xrt?Lang=zh-TW");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warm-up request failed, continuing anyway.");
            }
        }

        /// <summary>
        /// 下載內容並確認是 CSV。若拿到防爬蟲挑戰頁（HTML）就重試，全部重試都被擋則回傳 null。
        /// </summary>
        private async Task<string> FetchWithRetryAsync(HttpClient httpClient, string url)
        {
            const int MaxAttempts = 3;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                string content = await httpClient.GetStringAsync(url);

                if (!IsChallengePage(content))
                {
                    return content;
                }

                _logger.LogWarning($"Anti-bot challenge page received (attempt {attempt}/{MaxAttempts}) for {url}");

                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                    await WarmUpAsync(httpClient);
                }
            }

            return null;
        }

        /// <summary>
        /// 台銀被擋時會以 HTTP 200 回傳 HTML 挑戰頁，內容不是 CSV。
        /// </summary>
        private static bool IsChallengePage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return true;
            }

            // 查無資料（例如假日）是正常回應，交給呼叫端往前一天重找
            if (content.Contains("很抱歉，本次查詢找不到任何一筆資料！"))
            {
                return false;
            }

            string head = content.Length > 500 ? content.Substring(0, 500) : content;
            return head.IndexOf("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) >= 0
                || head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                || head.IndexOf("Challenge Validation", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
    public class MultiResponse
    {
        [CosmosDBOutput("TSAPI", "BankTaiwanSpotRate",
        Connection = "CosmosDbConnectionString", CreateIfNotExists = true)]
        public List<BankTaiwanSpotRate> Document { get; set; }
    }
    public class BankTaiwanSpotRate
    {
        public string Date { get; set; }
        public string Currency { get; set; }
        public decimal SpotRateBuying { get; set; }
        public decimal SpotRateSelling { get; set; }
        public int pkid { get; set; }
        public string id { get; set; }
    }
    public class FXRateCsvPoco
    {
        public string 幣別 { get; set; }
        public string 匯率 { get; set; }
        public decimal 現金 { get; set; }
        public decimal 即期 { get; set; }
        public decimal 遠期10天 { get; set; }
        public decimal 遠期30天 { get; set; }
        public decimal 遠期60天 { get; set; }
        public decimal 遠期90天 { get; set; }
        public decimal 遠期120天 { get; set; }
        public decimal 遠期150天 { get; set; }
        public decimal 遠期180天 { get; set; }

        public string 匯率1 { get; set; }
        public decimal 現金1 { get; set; }
        public decimal 即期1 { get; set; }
        public decimal 遠期10天1 { get; set; }
        public decimal 遠期30天1 { get; set; }
        public decimal 遠期60天1 { get; set; }
        public decimal 遠期90天1 { get; set; }
        public decimal 遠期120天1 { get; set; }
        public decimal 遠期150天1 { get; set; }
        public decimal 遠期180天1 { get; set; }


    }
    public sealed class FXRateCsvPocoMap : ClassMap<FXRateCsvPoco>
    {
        public FXRateCsvPocoMap()
        {
            Map(m => m.幣別);
            Map(m => m.匯率).Name("匯率").NameIndex(0);
            Map(m => m.現金).Name("現金").NameIndex(0);
            Map(m => m.即期).Name("即期").NameIndex(0);
            Map(m => m.遠期10天).Name("遠期10天").NameIndex(0);
            Map(m => m.遠期30天).Name("遠期30天").NameIndex(0);
            Map(m => m.遠期60天).Name("遠期60天").NameIndex(0);
            Map(m => m.遠期90天).Name("遠期90天").NameIndex(0);
            Map(m => m.遠期120天).Name("遠期120天").NameIndex(0);
            Map(m => m.遠期150天).Name("遠期150天").NameIndex(0);
            Map(m => m.遠期180天).Name("遠期180天").NameIndex(0);
            Map(m => m.匯率1).Name("匯率").NameIndex(1);
            Map(m => m.現金1).Name("現金").NameIndex(1);
            Map(m => m.即期1).Name("即期").NameIndex(1);
            Map(m => m.遠期10天1).Name("遠期10天").NameIndex(1);
            Map(m => m.遠期30天1).Name("遠期30天").NameIndex(1);
            Map(m => m.遠期60天1).Name("遠期60天").NameIndex(1);
            Map(m => m.遠期90天1).Name("遠期90天").NameIndex(1);
            Map(m => m.遠期120天1).Name("遠期120天").NameIndex(1);
            Map(m => m.遠期150天1).Name("遠期150天").NameIndex(1);
            Map(m => m.遠期180天1).Name("遠期180天").NameIndex(1);

        }
    }

    public class MyInfo
    {
        public MyScheduleStatus ScheduleStatus { get; set; }

        public bool IsPastDue { get; set; }
    }

    public class MyScheduleStatus
    {
        public DateTime Last { get; set; }

        public DateTime Next { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
