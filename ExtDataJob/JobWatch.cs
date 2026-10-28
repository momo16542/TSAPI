using System.Text;
using Microsoft.Extensions.Logging;

namespace ExtDataJob;

/// <summary>
/// JobWatch 回報（https://jobs.stonedemo.com）：開始、成功、失敗各打一次，
/// 控制台看得到每次執行的狀態、耗時與訊息，該跑沒跑也會通知。
///
///   · 回報網址等同密碼，放環境變數（Container Apps Job 設成 secret），沒設就不回報。
///   · **回報失敗絕不讓 job 失敗**：只寫 log，跟 Telegram 通知同一個原則。
/// </summary>
internal sealed class JobWatch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string? _url;
    private readonly ILogger _logger;
    // 同一次執行的 start／success／fail 帶同一個 rid，JobWatch 才配得起來
    private readonly string _rid = Guid.NewGuid().ToString();

    public JobWatch(string? url, ILogger logger)
    {
        _url = string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        _logger = logger;
    }

    public Task StartAsync() => PingAsync("start");

    public Task SuccessAsync(string? message = null) => PingAsync("success", message);

    public Task FailAsync(string? message, int? exitCode = null) =>
        PingAsync("fail", message, exitCode is int code ? $"&exit_code={code}" : "");

    private async Task PingAsync(string kind, string? text = null, string query = "")
    {
        if (_url is null) return;
        // 伺服器只存 8,000 字，錯誤通常在結尾，所以留後段
        if (text is { Length: > 8000 }) text = text[^8000..];

        try
        {
            using var response = await Http.PostAsync(
                $"{_url}/{kind}?rid={_rid}{query}",
                new StringContent(text ?? "", Encoding.UTF8, "text/plain"));
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("JobWatch 回報 {Kind} 失敗 ({Status})", kind, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JobWatch 回報 {Kind} 失敗（不影響 job 結果）", kind);
        }
    }
}
