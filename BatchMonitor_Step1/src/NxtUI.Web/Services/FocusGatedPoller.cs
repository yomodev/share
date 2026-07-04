namespace NxtUI.Web.Services;

/// <summary>
/// Wraps the Start()/Stop() + PeriodicTimer + CancellationTokenSource boilerplate that
/// Home.razor and LogBrowser.razor each hand-rolled independently for "keep refreshing
/// on an interval, but only while the page/section is actually being looked at."
/// Visibility/focus checks stay in the caller's <paramref name="onTick"/> callback —
/// this class only owns the timer lifecycle, not the definition of "visible."
/// </summary>
public sealed class FocusGatedPoller : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task> _onTick;
    private readonly Action<Exception>? _onError;
    private CancellationTokenSource? _cts;

    public FocusGatedPoller(TimeSpan interval, Func<CancellationToken, Task> onTick, Action<Exception>? onError = null)
    {
        _interval = interval;
        _onTick   = onTick;
        _onError  = onError;
    }

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return; // already running
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Start if <paramref name="shouldRun"/>, stop otherwise — for call sites that
    /// re-evaluate "should polling be active right now" from several places (selection
    /// changed, a panel opened, focus changed) rather than calling Start/Stop directly.</summary>
    public void SetActive(bool shouldRun)
    {
        if (shouldRun) Start();
        else Stop();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await _onTick(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _onError?.Invoke(ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Stop();
}
