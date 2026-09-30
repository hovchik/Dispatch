using System.Diagnostics;
using Dispatch.Domain;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols;

/// <summary>
/// Collects the messages of a streaming session, forwards them live to the UI, and decides when a non-interactive
/// session (collection runner, CLI) should stop listening.
/// </summary>
internal sealed class MessageLog : IDisposable
{
    private readonly ExecutionContext _context;
    private readonly int _maxMessages;
    private readonly List<StreamMessage> _messages = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop;
    private int _received;
    private long _receivedBytes;

    public MessageLog(ExecutionContext context, double listenSeconds, int maxMessages, CancellationToken cancellationToken)
    {
        _context = context;
        _maxMessages = maxMessages;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // In the UI the session stays open until the user disconnects; elsewhere it listens for a bounded time.
        if (!context.Interactive && listenSeconds > 0)
            _stop.CancelAfter(TimeSpan.FromSeconds(listenSeconds));
        Stopwatch = Stopwatch.StartNew();
    }

    /// <summary>Cancelled when the user disconnects, the listen time is over, or enough messages arrived.</summary>
    public CancellationToken Token => _stop.Token;

    public Stopwatch Stopwatch { get; }

    public IReadOnlyList<StreamMessage> Messages
    {
        get { lock (_gate) return _messages.ToList(); }
    }

    public int ReceivedCount => _received;
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);

    public void Sent(string content, string? label = null) => Add(StreamMessage.Sent(content, label));
    public void Info(string content) => Add(StreamMessage.Info(content));
    public void Failure(string content) => Add(StreamMessage.Failure(content));

    public void Received(string content, string? label = null, long bytes = -1)
    {
        Add(StreamMessage.Received(content, label));
        Interlocked.Add(ref _receivedBytes, bytes >= 0 ? bytes : content.Length);
        if (Interlocked.Increment(ref _received) >= _maxMessages && _maxMessages > 0)
            Stop();
    }

    public void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Add(StreamMessage message)
    {
        lock (_gate)
            _messages.Add(message);
        _context.Report(message);
    }

    public void Dispose() => _stop.Dispose();
}
