using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TgosRelay;

// TGOS 轉發器。對外只有三個 GET：
//   /healthz             → 200 {status, version, tgosConfigured, geoConfigured}（不打 TGOS）
//   /geocode?address=X   → header X-Relay-Key 驗證 → 1 req/s 節流 → TGOS 正查 → {found, lon, lat, matchType, tgosAddress, raw}
//   /reverse?lat=&lon=   → 同樣的驗證與節流 → TGOS 反查（坐標回傳門牌）→ {found, address, lon, lat, matchType, raw}
// 兩支查詢共用同一個 Throttle：打的是同一個 TGOS 帳號，兩支「加起來」才是 1 req/s。
// 只聽 127.0.0.1:5000；對外 443 由 Caddy 反代並自動簽憑證（infra/Caddyfile）。
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5000");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });

// ⚠️ 關掉框架的逐請求 log（Information 級）：它會把**完整 URL 連同查詢字串**印出來，
// 也就是 /geocode?address=<客戶地址>、/reverse?lat=&lon=<工地座標> 全都會落進 journald。
// 這與本服務「log 不記地址」的設計牴觸（TGOS 使用規定第七條第 8 款：資料不得對外流通，log 也算）
// ——2026-09-09 發現 /geocode 自上線起一直如此。
// 代價是少了框架的逐請求記錄，但兩支端點各自都記了「結果＋耗時」那一行（不含地址與座標），
// 維運要看的資訊沒有少；Warning 以上（含未處理例外、啟動失敗）仍照印。
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

var cfg = RelayConfig.FromEnvironment();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(new Throttle(TimeSpan.FromMilliseconds(cfg.MinIntervalMs)));
builder.Services.AddHttpClient<TgosClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TsERP-TgosRelay/1.0 (yanyue.io)");
});

var app = builder.Build();
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
var log = app.Logger;
log.LogInformation("tgos-relay {Version} 啟動；RELAY_KEY={RelayKey} TGOS 金鑰={Tgos} GEO 金鑰={Geo} XY_SWAP={Swap}/{GeoSwap} 間隔={Ms}ms 里鄰={Village} 距離門檻={MaxDist}m",
    version, cfg.RelayKey已設 ? "已設" : "未設", cfg.Tgos金鑰已設 ? "已設" : "未設", cfg.Geo金鑰已設 ? "已設" : "未設",
    cfg.XySwap, cfg.GeoXySwap, cfg.MinIntervalMs, cfg.GeoIncludeVillage ? "含" : "不含",
    cfg.GeoMaxDistanceM > 0 ? cfg.GeoMaxDistanceM.ToString() : "不限制");

// geoConfigured 與 tgosConfigured 分開報：兩支服務各自申請、可能一支核准了另一支還沒
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version, tgosConfigured = cfg.Tgos金鑰已設, geoConfigured = cfg.Geo金鑰已設 }));

app.MapGet("/geocode", async (HttpContext ctx, string? address, TgosClient tgos, Throttle throttle, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();

    // 設定缺漏回 503＋指名鍵：是設定錯，不是暫時性故障，重試不會好（沿用 YYAPI 2026-09-03 審查 S4 的原則）
    if (!cfg.RelayKey已設)
        return Results.Json(new { error = "轉發器未設定 RELAY_KEY" }, statusCode: 503);
    if (!金鑰相符(ctx.Request.Headers["X-Relay-Key"].ToString(), cfg.RelayKey))
    {
        log.LogWarning("401 來源 {Ip}", ctx.Connection.RemoteIpAddress);
        return Results.Json(new { error = "X-Relay-Key 錯誤或缺少" }, statusCode: 401);
    }
    if (!cfg.Tgos金鑰已設)
        return Results.Json(new { error = "轉發器未設定 TGOS_APPID／TGOS_APIKEY（TGOS 核准後填入 /etc/tgos-relay.env）" }, statusCode: 503);

    var 地址 = (address ?? "").Trim();
    if (地址.Length == 0) return Results.Json(new { error = "缺少參數 address" }, statusCode: 400);
    if (地址.Length > 200) return Results.Json(new { error = "address 超過 200 字" }, statusCode: 400);

    try
    {
        var hit = await throttle.RunAsync(c => tgos.QueryAsync(地址, c), ct);
        // 不記完整地址（使用規定第七條第 8 款：資料不得對外流通；log 也算），只記長度與結果
        log.LogInformation("geocode len={Len} found={Found} {Ms}ms", 地址.Length, hit != null, sw.ElapsedMilliseconds);
        return hit == null
            ? Results.Ok(new { found = false })
            : Results.Ok(new { found = true, lon = hit.Lon, lat = hit.Lat, matchType = hit.MatchType, tgosAddress = hit.FullAddress, raw = hit.Raw });
    }
    catch (TgosUpstreamException ex)
    {
        log.LogError("502 {Msg} {Ms}ms", ex.Message, sw.ElapsedMilliseconds);
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
});

app.MapGet("/reverse", async (HttpContext ctx, string? lat, string? lon, TgosClient tgos, Throttle throttle, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();

    // 檢查順序與 /geocode 完全一致（設定 → 身分 → 該服務的金鑰 → 參數）
    if (!cfg.RelayKey已設)
        return Results.Json(new { error = "轉發器未設定 RELAY_KEY" }, statusCode: 503);
    if (!金鑰相符(ctx.Request.Headers["X-Relay-Key"].ToString(), cfg.RelayKey))
    {
        log.LogWarning("401 來源 {Ip}", ctx.Connection.RemoteIpAddress);
        return Results.Json(new { error = "X-Relay-Key 錯誤或缺少" }, statusCode: 401);
    }
    // 反查用的是另一支服務的金鑰：正查有金鑰不代表反查有，訊息要指名 GEO 這組，
    // 否則維運者會去看已經填好的 TGOS_APPID 然後以為設定沒問題
    if (!cfg.Geo金鑰已設)
        return Results.Json(new { error = "轉發器未設定 TGOS_GEO_APPID／TGOS_GEO_APIKEY（坐標回傳門牌服務要另外申請，核准後填入 /etc/tgos-relay.env）" }, statusCode: 503);

    if (!解析座標(lat, out var 緯度, -90, 90))
        return Results.Json(new { error = "參數 lat 缺少或不是 -90~90 的數字" }, statusCode: 400);
    if (!解析座標(lon, out var 經度, -180, 180))
        return Results.Json(new { error = "參數 lon 缺少或不是 -180~180 的數字" }, statusCode: 400);

    try
    {
        var hit = await throttle.RunAsync(c => tgos.ReverseAsync(緯度, 經度, c), ct);

        // DIS 缺欄不擋（上游少給一個欄位不該讓整批查不到），但記一筆給維運看
        if (hit != null && !hit.Dis.HasValue)
            log.LogInformation("reverse dis缺欄 {Ms}ms", sw.ElapsedMilliseconds);

        // 太遠視同查無（不是故障）：TGOS 反查回的是「最近門牌」、沒有距離上限，
        // 工地空曠時可能回幾公里外的門牌卻照樣標成門牌精度（2026-09-09 使用者裁決，預設 200m）
        if (hit != null && TgosReverseGate.太遠(hit.Dis, cfg.GeoMaxDistanceM))
        {
            // 只記距離與門檻，不記座標與地址（使用規定第七條第 8 款；同下方理由）
            log.LogInformation("reverse tooFar dist={Dist}m 門檻={Threshold}m {Ms}ms", hit.Dis!.Value, cfg.GeoMaxDistanceM, sw.ElapsedMilliseconds);
            return Results.Ok(new { found = false, tooFar = true, distanceM = hit.Dis!.Value });
        }

        // 與 /geocode 同一條理由（使用規定第七條第 8 款）：座標與地址都不進 log，只記結果與耗時。
        // 反查的座標本身就是個人／工地位置，比地址更敏感。
        log.LogInformation("reverse found={Found} {Ms}ms", hit != null, sw.ElapsedMilliseconds);
        return hit == null
            ? Results.Ok(new { found = false })
            : Results.Ok(new { found = true, address = hit.Address, lon = hit.Lon, lat = hit.Lat, matchType = hit.MatchType, distanceM = hit.Dis, raw = hit.Raw });
    }
    catch (TgosUpstreamException ex)
    {
        log.LogError("502 {Msg} {Ms}ms", ex.Message, sw.ElapsedMilliseconds);
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
});

app.Run();

// 座標一律用 InvariantCulture 解析：查詢字串的格式是合約的一部分，不能跟著 VM 的地區設定跑
// （"25,0377" 在 de-DE 下會被解成 250377）。NaN／Infinity 由範圍比較擋掉（與任何數字比大小都是 false）。
static bool 解析座標(string? 值, out double 結果, double 下限, double 上限)
{
    結果 = 0;
    var s = (值 ?? "").Trim();
    if (s.Length == 0) return false;
    if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out 結果)) return false;
    return 結果 >= 下限 && 結果 <= 上限;
}

static bool 金鑰相符(string 來的, string 設定的)
{
    if (string.IsNullOrEmpty(來的)) return false;
    var a = Encoding.UTF8.GetBytes(來的);
    var b = Encoding.UTF8.GetBytes(設定的);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}
