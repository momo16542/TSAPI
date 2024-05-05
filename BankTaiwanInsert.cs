using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using CsvHelper.Configuration;
using Microsoft.Azure.WebJobs.Extensions.CosmosDB;
using CsvHelper;
using System.Globalization;

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
        // [CosmosDBOutput("TSAPI", "BankTaiwanSpotRate",
        // Connection = "CosmosDbConnectionString", CreateIfNotExists = true)]
        public async Task<MultiResponse> Run([TimerTrigger("0 0 18 * * *")] MyInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
            _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");            
            var list = await DownloadAndParseExchangeRates();
            _logger.LogInformation($"fx rate completed");
            return new MultiResponse() { Document = list };
        }
        private async Task<List<BankTaiwanSpotRate>> DownloadAndParseExchangeRates()
        {
            List<BankTaiwanSpotRate> list = new List<BankTaiwanSpotRate>();
            string csvData;
            using (var httpClient = new HttpClient())
            {
                csvData = await httpClient.GetStringAsync("https://rate.bot.com.tw/xrt/flcsv/0/day");
            }

            var stringReader = new StringReader(csvData);
            using (var csvReader = new CsvReader(stringReader, CultureInfo.InvariantCulture))
            {
                csvReader.Context.RegisterClassMap<FXRateCsvPocoMap>();
                var records = csvReader.GetRecords<FXRateCsvPoco>();
                foreach (var item in records)
                {
                    list.Add(new BankTaiwanSpotRate()
                    {
                        Date = DateTime.UtcNow.ToString("yyyy/MM/dd"),
                        Currency = item.幣別,
                        SpotRateBuying = item.即期,
                        SpotRateSelling = item.即期1,
                        id = System.Guid.NewGuid().ToString()
                    });

                }

                return list;
            }
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
