using System.Data;
using ExtDataJob;
using LogicBll.ExternalData;
using LogicBll.ExternalData.Sources;
using LogicBll.ExternalData.Ttvma;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TsErp.ExternalData.Central;
using TsErp.ExternalData.Parsing.Npoi;

// 外部資料抓取 job：下載 → 解析 → 寫中央 Azure SQL。
// 殼照 BotRateJob（環境變數設定、Telegram 失敗通知、非 0 結束碼＝Container Apps Job 失敗）。

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger("ExtDataJob");
var notifier = new Notifier(logger);
var summary = new List<string>();

try
{
    // ── 1. 抓取 ─────────────────────────────────────────────
    var options = new ExternalFetchOptions
    {
        RootDir = Config.DataRoot,
        // 公會每月 10~18 日才公布上月數據，所以排程抓的檔屬「上個月」
        Period = DateTime.Today.AddMonths(-1),
        Force = Config.ForceRefetch,
    };
    logger.LogInformation("抓取開始：root={Root} period={Period:yyyy-MM} force={Force}",
        options.RootDir, options.Period, options.Force);

    var run = await ExternalDataFetchBll.CreateDefault().Run(options, Config.SourceKeys);
    foreach (var r in run.Results)
    {
        logger.LogInformation("  {Key,-14} {Status,-8} {Bytes,10:N0} bytes  {Msg}",
            r.SourceKey, r.Status, r.ByteCount, r.Message);
    }
    if (run.FailedCount > 0)
    {
        // 有來源失敗就整支算失敗——寧可吵，也不要靜靜地少一份資料
        throw new InvalidOperationException(
            $"{run.FailedCount} 個來源抓取失敗：" +
            string.Join("；", run.Results.Where(x => !x.IsOk).Select(x => $"{x.SourceKey} {x.Message}")));
    }

    // ── 2. 解析 ＋ 3. 寫入 ──────────────────────────────────
    // **只解析近幾個月**：2026-09-01 實測，整包 285,438 列寫進 Basic 層級要 7.4 分鐘，
    // 而公會每月只新增約 1,000 列。首次灌檔才需要全量（不設 EXTDATA_SINCE_MONTH）。
    var since = Config.SinceMonth ?? DateTime.Today.AddMonths(-3);
    logger.LogInformation("解析起始月：{Since:yyyy-MM}{Note}", since,
        Config.SinceMonth is null ? "（預設近三個月；首次灌檔請設 EXTDATA_SINCE_MONTH=2002-08）" : "");

    var writer = Config.DryRun ? null : new CentralDbWriter(Config.SqlConnectionString, 900, AzureToken.ForSqlDatabase);

    // 用 IsOk 而不是只看 Success：Skipped 代表「本期已抓過、檔案就在本機」，
    // 一樣要往下解析寫入——否則排程重跑（或上次寫入失敗後重試）會靜靜地什麼都沒做。
    // 2026-09-01 實跑第二次時就踩到：三個來源全 Skipped → 「沒有需要寫入的資料」。
    foreach (var result in run.Results.Where(x => x.IsOk && !string.IsNullOrEmpty(x.SavedPath)))
    {
        var 車輛別 = TtvmaLongFormatEngine.車輛別FromSourceKey(result.SourceKey);

        if (車輛別 is not null)
        {
            var warnings = new List<string>();
            using var wb = NpoiSpreadsheetWorkbook.Open(result.SavedPath);
            var rows = TtvmaLongFormatEngine.ParseWorkbook(
                wb, 車輛別, Path.GetFileName(result.SavedPath), warnings, since, result.FetchedAt);
            foreach (var w in warnings) logger.LogWarning("  解析警告：{W}", w);

            logger.LogInformation("  {Key}：解析 {Count} 列", result.SourceKey, rows.Count);
            if (writer is null) { summary.Add($"{車輛別} 解析 {rows.Count} 列（DRY_RUN）"); continue; }

            var merged = writer.UpsertTtvma(rows);
            logger.LogInformation("  {Key}：{Merged}", result.SourceKey, merged);
            summary.Add($"{車輛別} 新增 {merged.新增}／更新 {merged.更新}");
        }
        else if (result.SourceKey == "METALPRICE")
        {
            // 行情來源抓取時就已解析成 JSON 落地，這裡直接讀回來
            var rows = JsonConvert.DeserializeObject<List<MetalPriceRow>>(
                           File.ReadAllText(result.SavedPath)) ?? new List<MetalPriceRow>();
            logger.LogInformation("  {Key}：讀入 {Count} 列", result.SourceKey, rows.Count);
            if (writer is null) { summary.Add($"行情 {rows.Count} 列（DRY_RUN）"); continue; }

            var merged = writer.UpsertMetalPrice(MetalPriceTable.Build(rows, result.FetchedAt));
            logger.LogInformation("  {Key}：{Merged}", result.SourceKey, merged);
            summary.Add($"行情 新增 {merged.新增}／更新 {merged.更新}");

            // 品項主檔的權威來源是程式端的 MetalItemCatalog，跟著行情一起同步
            var itemMerged = writer.UpsertMetalItem(MetalPriceTable.BuildItems());
            logger.LogInformation("  品項主檔：{Merged}", itemMerged);
        }
        else
        {
            logger.LogWarning("  {Key}：沒有對應的寫入路徑，略過", result.SourceKey);
        }
    }

    var text = summary.Count > 0 ? string.Join("；", summary) : "沒有需要寫入的資料";
    logger.LogInformation("完成：{Text}", text);
    if (!Config.DryRun) await notifier.SendAsync($"外部資料更新成功：{text}");
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "外部資料更新失敗");
    if (!Config.DryRun)
    {
        await notifier.SendAsync($"外部資料更新失敗：{ex.GetType().Name} {ex.Message}");
    }
    return 1;   // 非 0 讓 Container Apps Job 標記為失敗
}

/// <summary>把行情列轉成中央 staging 表要的 DataTable（欄名必須與 staging 一致）。</summary>
internal static class MetalPriceTable
{
    public static DataTable Build(IEnumerable<MetalPriceRow> rows, DateTime? 抓取時間)
    {
        var t = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
                 {
                     ("日期", typeof(DateTime)), ("金屬", typeof(string)), ("名稱", typeof(string)),
                     ("交易所", typeof(string)), ("品號", typeof(string)), ("報價單位", typeof(string)),
                     ("收盤價", typeof(decimal)), ("漲跌", typeof(decimal)), ("漲跌百分比", typeof(decimal)),
                     ("開盤", typeof(decimal)), ("最高", typeof(decimal)), ("最低", typeof(decimal)),
                     ("成交量", typeof(decimal)), ("未平倉", typeof(decimal)),
                     ("來源檔", typeof(string)), ("抓取時間", typeof(DateTime)),
                 })
        {
            t.Columns.Add(name, type);
        }

        foreach (var r in rows)
        {
            if (!DateTime.TryParse(r.日期, out var d)) continue;   // 日期解不出來的列不進 DB，寧缺勿錯
            t.Rows.Add(d, r.金屬 ?? "", r.名稱 ?? "", r.交易所 ?? "",
                Obj(r.品號), Obj(r.報價單位),
                // 沒有報價就是 NULL，不可寫成 0（同公會數量欄的語意）
                Dec(r.收盤價), Dec(r.漲跌), Dec(r.漲跌百分比), Dec(r.開盤),
                Dec(r.最高), Dec(r.最低), Dec(r.成交量), Dec(r.未平倉),
                DBNull.Value, (object?)抓取時間 ?? DBNull.Value);
        }
        return t;
    }

    /// <summary>品項主檔：版控源頭是程式端的 MetalItemCatalog，每次同步照它覆寫。</summary>
    public static DataTable BuildItems()
    {
        var t = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
                 {
                     ("品號", typeof(string)), ("金屬", typeof(string)), ("交易所", typeof(string)),
                     ("名稱", typeof(string)), ("報價單位", typeof(string)), ("對應品號", typeof(string)),
                     ("是否啟用", typeof(string)), ("備註", typeof(string)),
                 })
        {
            t.Columns.Add(name, type);
        }
        foreach (var i in MetalItemCatalog.All)
        {
            t.Rows.Add(i.品號, Obj(i.金屬), i.交易所, i.名稱, Obj(i.報價單位),
                DBNull.Value, "Y", DBNull.Value);
        }
        return t;
    }

    private static object Obj(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s;
    private static object Dec(double? d) => d.HasValue ? (decimal)d.Value : DBNull.Value;
}
