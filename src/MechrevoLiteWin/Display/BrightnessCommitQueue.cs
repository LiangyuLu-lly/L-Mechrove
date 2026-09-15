namespace MechrevoLite.Display;

internal sealed class BrightnessCommitQueue : IDisposable
{
    private readonly Func<int, CancellationToken, Task> _writer;
    private readonly Action<Exception>? _onError;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _requestCts;
    private TaskCompletionSource<bool> _idle = CompletedSource();
    private int? _latestValue;
    private bool _disposed;

    public BrightnessCommitQueue(
        Func<int, CancellationToken, Task> writer,
        TimeSpan debounce,
        Action<Exception>? onError = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        if (debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        _debounce = debounce;
        _onError = onError;
    }

    public bool HasPendingOrRunning
    {
        get
        {
            lock (_gate) return _requestCts is not null;
        }
    }

    public void Submit(int value) => Start(value, _debounce);

    public void Flush()
    {
        int value;
        lock (_gate)
        {
            if (_disposed || !_latestValue.HasValue) return;
            value = _latestValue.Value;
        }

        Start(value, TimeSpan.Zero);
    }

    internal Task WaitForIdleAsync()
    {
        lock (_gate) return _requestCts is null ? Task.CompletedTask : _idle.Task;
    }

    public void Dispose()
    {
        CancellationTokenSource? request;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            request = _requestCts;
            _requestCts = null;
            _latestValue = null;
            _idle.TrySetResult(true);
        }

        request?.Cancel();
        _disposeCts.Cancel();
    }

    private void Start(int value, TimeSpan delay)
    {
        CancellationTokenSource? previous;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        lock (_gate)
        {
            if (_disposed)
            {
                request.Dispose();
                throw new ObjectDisposedException(nameof(BrightnessCommitQueue));
            }

            previous = _requestCts;
            _requestCts = request;
            _latestValue = value;
            if (_idle.Task.IsCompleted)
                _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous?.Cancel();
        _ = CommitAsync(value, delay, request);
    }

    private async Task CommitAsync(int value, TimeSpan delay, CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(delay, request.Token).ConfigureAwait(false);
            await _writeLock.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                if (!request.IsCancellationRequested)
                    await _writer(value, _disposeCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try { _onError?.Invoke(ex); }
            catch (Exception callbackEx)
            {
                Logger.WriteLine("Brightness commit error callback failed: " + callbackEx.GetType().Name + " " + callbackEx.Message);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_requestCts, request))
                {
                    _requestCts = null;
                    _latestValue = null;
                    _idle.TrySetResult(true);
                }
            }
            request.Dispose();
        }
    }

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }
}
