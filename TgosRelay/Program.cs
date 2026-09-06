using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TgosRelay;

// TGOS 轉發器。對外只有兩個 GET：
//   /healthz            → 200 {status, version, tgosConfigured}（不打 TGOS）
//   /geocode?address=X  → header X-Relay-Key 驗證 → 1 req/s 節流 → TGOS → {found, lon, lat, matchType, tgosAddress, raw}
// 只聽 127.0.0.1:5000；對外 443 由 Caddy 反代並自動簽憑證（infra/Caddyfile）。
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5000");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });

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
log.LogInformation("tgos-relay {Version} 啟動；RELAY_KEY={RelayKey} TGOS 金鑰={Tgos} XY_SWAP={Swap} 間隔={Ms}ms",
    version, cfg.RelayKey已設 ? "已設" : "未設", cfg.Tgos金鑰已設 ? "已設" : "未設", cfg.XySwap, cfg.MinIntervalMs);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version, tgosConfigured = cfg.Tgos金鑰已設 }));

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

app.Run();

static bool 金鑰相符(string 來的, string 設定的)
{
    if (string.IsNullOrEmpty(來的)) return false;
    var a = Encoding.UTF8.GetBytes(來的);
    var b = Encoding.UTF8.GetBytes(設定的);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}
