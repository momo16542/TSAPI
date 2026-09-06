namespace TgosRelay;

/// <summary>
/// 全行程共用的最小間隔閘：不管幾個請求同時進來，打 TGOS 一律排成一條隊伍，兩次之間至少隔 <c>MinInterval</c>。
/// 這台 VM 只有這一個行程在打 TGOS，所以行程內的閘就是全域的閘（YYAPI 端的節流反而管不住多實例）。
/// </summary>
public sealed class Throttle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minInterval;
    private DateTime _上次送出 = DateTime.MinValue;

    public Throttle(TimeSpan minInterval) => _minInterval = minInterval;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var 距上次 = DateTime.UtcNow - _上次送出;
            if (距上次 < _minInterval)
                await Task.Delay(_minInterval - 距上次, ct).ConfigureAwait(false);
            _上次送出 = DateTime.UtcNow;
            return await work(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
