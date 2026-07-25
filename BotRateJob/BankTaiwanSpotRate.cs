using CsvHelper.Configuration;

namespace BotRateJob;

/// <summary>
/// 寫入 Cosmos 的文件結構，欄位名稱與原本 Function 寫入的完全一致，下游查詢不用改。
/// </summary>
public sealed class BankTaiwanSpotRate
{
    public string Date { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal SpotRateBuying { get; set; }
    public decimal SpotRateSelling { get; set; }
    public int pkid { get; set; }
    public string id { get; set; } = "";
}

/// <summary>
/// 台銀 CSV 欄位。標題列的「匯率/現金/即期/遠期…」會出現兩次（買入一組、賣出一組），
/// 因此靠 NameIndex 區分，與原本 Function 的對應方式相同。
/// </summary>
public sealed class FXRateCsvPoco
{
    public string 幣別 { get; set; } = "";
    public string 匯率 { get; set; } = "";
    public decimal 現金 { get; set; }
    public decimal 即期 { get; set; }
    public decimal 遠期10天 { get; set; }
    public decimal 遠期30天 { get; set; }
    public decimal 遠期60天 { get; set; }
    public decimal 遠期90天 { get; set; }
    public decimal 遠期120天 { get; set; }
    public decimal 遠期150天 { get; set; }
    public decimal 遠期180天 { get; set; }

    public string 匯率1 { get; set; } = "";
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
