namespace Grimmcair.Desktop.Services;

// Recovers a suspended session with verify only. It never logs in or retries a cloud mutation.
public sealed class DesktopApiVerificationLoop : IDisposable
{
    private readonly DesktopApiSession _session;
    private readonly TimeProvider _clock;
    private readonly Func<Task> _verified;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly SemaphoreSlim _attempt = new(1, 1);
    private readonly CancellationTokenSource _disposedCancellation = new();
    private readonly object _retryLock = new();
    private long _retryStartedAt;
    private TimeSpan? _retryDelay;
    private int _failures;
    private int _started, _disposed;

    public DesktopApiVerificationLoop(DesktopApiSession session, Func<Task> verified)
    {
        _session = session;
        _clock = session.Clock;
        _verified = verified;
        _session.Changed += Wake;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("后台验证已启动。");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposedCancellation.Token);
        var token = lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var state = _session.State;
                if (IsTerminal(state)) return;
                var delay = TimeUntilNextAttempt;
                if (delay > TimeSpan.Zero)
                {
                    await WaitForChangeAsync(delay, token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    if (!await AttemptAsync(token).ConfigureAwait(false))
                        await WaitForChangeAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (DesktopApiException) { /* Session state and the retry deadline were updated by AttemptAsync. */ }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _session.Changed -= Wake; }
    }

    // User recovery skips our backoff, while retaining any server Retry-After deadline.
    // False means that a verification is already running or this session needs an explicit new login.
    public async Task<bool> RetryNowAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposedCancellation.Token);
        if (_session.State != DesktopSessionState.PendingVerification) return false;
        return await AttemptAsync(lifetime.Token).ConfigureAwait(false);
    }

    internal TimeSpan TimeUntilNextAttempt
    {
        get
        {
            var state = _session.State;
            if (state == DesktopSessionState.Allowed) return _session.TimeUntilVerification;
            if (state != DesktopSessionState.PendingVerification) return TimeSpan.FromMinutes(5);
            lock (_retryLock)
            {
                // A failed cloud request can suspend an otherwise healthy hourly session.
                if (_retryDelay is null) { _failures = 1; _retryStartedAt = _clock.GetTimestamp(); _retryDelay = TimeSpan.FromSeconds(30); }
                var remaining = _retryDelay.Value - _clock.GetElapsedTime(_retryStartedAt);
                return Maximum(remaining, _session.TimeUntilRetryAllowed, TimeSpan.Zero);
            }
        }
    }

    private async Task<bool> AttemptAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _disposed) != 0 || IsTerminal(_session.State)) return false;
        if (!await _attempt.WaitAsync(0, token).ConfigureAwait(false)) return false;
        try
        {
            DesktopAuthResponse? response;
            try { response = await _session.TryVerifyAsync(token).ConfigureAwait(false); }
            catch (DesktopApiException)
            {
                if (_session.State == DesktopSessionState.PendingVerification)
                {
                    lock (_retryLock)
                    {
                        _failures = Math.Min(_failures + 1, 5);
                        _retryDelay = TimeSpan.FromSeconds(_failures switch { 1 => 30, 2 => 60, 3 => 120, _ => 300 });
                        _retryStartedAt = _clock.GetTimestamp();
                    }
                }
                Wake();
                throw;
            }
            if (response is null) return false;
            lock (_retryLock) { _failures = 0; _retryDelay = null; }
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) == 0 && _session.IsAllowed)
            {
                // Refresh errors belong to the cloud/UI caller, and cannot become authentication failures.
                try { await _verified().ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { }
            }
            Wake();
            return true;
        }
        finally { _attempt.Release(); }
    }

    private async Task WaitForChangeAsync(TimeSpan delay, CancellationToken token)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Chunk very long server Retry-After values without shortening their effective deadline.
        if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
        var timer = Task.Delay(delay, _clock, waiting.Token);
        var changed = _changed.WaitAsync(waiting.Token);
        var completed = await Task.WhenAny(timer, changed).ConfigureAwait(false);
        waiting.Cancel();
        await completed.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private void Wake()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_session.State == DesktopSessionState.Allowed)
            lock (_retryLock) { _failures = 0; _retryDelay = null; }
        try { _changed.Release(); } catch (SemaphoreFullException) { }
    }

    private static bool IsTerminal(DesktopSessionState state) => state is
        DesktopSessionState.LoggedOut or DesktopSessionState.RequiresLogin or DesktopSessionState.Denied;
    private static TimeSpan Maximum(TimeSpan first, TimeSpan second, TimeSpan third)
        => first >= second && first >= third ? first : second >= third ? second : third;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.Changed -= Wake;
        _disposedCancellation.Cancel();
        // Semaphores may still be awaited by a finishing HTTP request; their managed state is released by GC.
    }

    // A manual retry is independent of RunAsync; shutdown must also wait for its HTTP/callback cleanup.
    public async Task StopAsync()
    {
        Dispose();
        await _attempt.WaitAsync().ConfigureAwait(false);
        _attempt.Release();
    }
}
