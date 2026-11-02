namespace Dispatch.Application.Webhooks;

/// <summary>Thread-safe, bounded store of received hooks (oldest dropped first) that wakes waiters when one arrives.</summary>
public sealed class WebhookLog(int capacity = 2000)
{
    private readonly Lock _gate = new();
    private readonly List<ReceivedWebhook> _hooks = [];
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _total;

    public int Capacity { get; } = Math.Max(1, capacity);

    /// <summary>Raised on the receiving thread for every hook (after it is stored).</summary>
    public event Action<ReceivedWebhook>? Added;

    /// <summary>Raised after <see cref="Clear"/>.</summary>
    public event Action? Cleared;

    public int Count
    {
        get
        {
            lock (_gate)
                return _hooks.Count;
        }
    }

    /// <summary>Everything ever added, including hooks that were dropped to stay within the capacity.</summary>
    public long Total => Interlocked.Read(ref _total);

    public void Add(ReceivedWebhook hook)
    {
        TaskCompletionSource arrived;
        lock (_gate)
        {
            _hooks.Add(hook);
            if (_hooks.Count > Capacity)
                _hooks.RemoveAt(0);
            Interlocked.Increment(ref _total);
            arrived = _arrived;
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        arrived.TrySetResult();
        Added?.Invoke(hook);
    }

    /// <summary>Oldest first.</summary>
    public List<ReceivedWebhook> Snapshot()
    {
        lock (_gate)
            return _hooks.ToList();
    }

    public ReceivedWebhook? Find(Guid id)
    {
        lock (_gate)
            return _hooks.FirstOrDefault(h => h.Id == id);
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
            return _hooks.RemoveAll(h => h.Id == id) > 0;
    }

    public void Clear()
    {
        lock (_gate)
            _hooks.Clear();
        Cleared?.Invoke();
    }

    /// <summary>Completes when the next hook arrives, or after <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitForNextAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task arrived;
        lock (_gate)
            arrived = _arrived.Task;
        if (timeout <= TimeSpan.Zero)
            return arrived.IsCompleted;
        var finished = await Task.WhenAny(arrived, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return finished == arrived;
    }

    /// <summary>
    /// Waits until a hook matching <paramref name="pathFilter"/> (see <see cref="WebhookMatcher"/>) that arrived after
    /// <paramref name="since"/> is present, at most until <paramref name="deadline"/>. Null when none arrived in time.
    /// </summary>
    public async Task<ReceivedWebhook?> WaitForAsync(string pathFilter, DateTimeOffset since, DateTimeOffset deadline, CancellationToken ct = default)
    {
        while (true)
        {
            Task arrived;
            ReceivedWebhook? match;
            lock (_gate)
            {
                match = _hooks.FirstOrDefault(h => h.ReceivedAt >= since && WebhookMatcher.Matches(h, pathFilter));
                arrived = _arrived.Task;
            }
            if (match is not null)
                return match;
            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
                return null;
            await Task.WhenAny(arrived, Task.Delay(remaining, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }
}
