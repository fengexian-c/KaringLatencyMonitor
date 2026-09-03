namespace KaringLatencyMonitor.App.Services;

public sealed class CollectorScheduler : IDisposable
{
    private CancellationTokenSource? _cancellationSource;
    private Task? _worker;

    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start(
        Func<CancellationToken, Task> collectAsync,
        Func<TimeSpan> getInterval,
        bool runImmediately)
    {
        Stop();
        _cancellationSource = new CancellationTokenSource();
        var token = _cancellationSource.Token;
        _worker = Task.Run(async () =>
        {
            if (runImmediately)
            {
                await RunSafelyAsync(collectAsync, token).ConfigureAwait(false);
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(getInterval(), token).ConfigureAwait(false);
                    await RunSafelyAsync(collectAsync, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }, token);
    }

    public void Stop()
    {
        var source = Interlocked.Exchange(ref _cancellationSource, null);
        source?.Cancel();
        source?.Dispose();
        _worker = null;
    }

    private static async Task RunSafelyAsync(
        Func<CancellationToken, Task> collectAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await collectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Collection errors are surfaced by the view model; the scheduler stays alive.
        }
    }

    public void Dispose() => Stop();
}
