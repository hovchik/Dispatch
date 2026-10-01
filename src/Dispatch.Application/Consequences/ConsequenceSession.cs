using System.Text.RegularExpressions;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Application.Testing;
using Dispatch.Application.Variables;
using Dispatch.Domain;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Application.Consequences;

/// <summary>
/// Runs the listeners behind a request's <see cref="MessageExpectation"/>s: each referenced streaming request (Kafka,
/// MQTT, AMQP, WebSocket, SSE, Socket.IO) is opened and subscribed <em>before</em> the request is sent, its messages are
/// collected, and after the response (and its extraction rules) the expectations are checked against the messages that
/// arrived after the request went out.
/// </summary>
internal sealed class ConsequenceSession : IAsyncDisposable
{
    /// <summary>Streaming kinds that can act as listeners.</summary>
    public static readonly IReadOnlySet<RequestKind> ListenerKinds = new HashSet<RequestKind>
    {
        RequestKind.Mqtt, RequestKind.Kafka, RequestKind.Amqp, RequestKind.WebSocket, RequestKind.Sse, RequestKind.SocketIo
    };

    /// <summary>How long to wait for a listener to confirm it is subscribed before sending the request anyway.</summary>
    internal static TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private readonly List<Listener> _listeners = [];
    private readonly List<(MessageExpectation Expectation, Listener? Listener, string? Problem)> _checks = [];
    private readonly CancellationTokenSource _stop;

    private ConsequenceSession(CancellationToken cancellationToken) =>
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    /// <summary>When the request that should cause the messages was sent; earlier messages don't count.</summary>
    public DateTimeOffset TriggeredAt { get; private set; } = DateTimeOffset.MaxValue;

    public void MarkTriggered() => TriggeredAt = DateTimeOffset.Now;

    /// <summary>Opens every listener the enabled expectations need and waits until they are subscribed. Null when there are none.</summary>
    public static async Task<ConsequenceSession?> StartAsync(ApiRequest request, IReadOnlyList<ApiRequest>? collectionRequests,
        Func<RequestKind, IProtocolExecutor?> executorFor, VariableContext variables, CancellationToken cancellationToken)
    {
        var expectations = request.Expectations.Where(e => e.Enabled).ToList();
        if (expectations.Count == 0)
            return null;

        var session = new ConsequenceSession(cancellationToken);
        var byId = new Dictionary<Guid, Listener>();
        foreach (var expectation in expectations)
        {
            var source = collectionRequests?.FirstOrDefault(r => r.Id == expectation.ListenerId);
            string? problem = null;
            if (source is null)
                problem = collectionRequests is null
                    ? "Message checks need the request to be in a collection (the listener is a saved request)."
                    : "The listener request was not found in the collection.";
            else if (!ListenerKinds.Contains(source.Kind))
                problem = $"\"{source.Name}\" is a {source.Kind} request; a listener must be MQTT, Kafka, AMQP, WebSocket, SSE or Socket.IO.";
            else if (source.Id == request.Id)
                problem = "A request cannot listen to itself.";
            else if (executorFor(source.Kind) is null)
                problem = $"{source.Kind} is not supported.";

            if (problem is not null)
            {
                session._checks.Add((expectation, null, problem));
                continue;
            }
            if (!byId.TryGetValue(source!.Id, out var listener))
            {
                listener = new Listener(source.Name);
                byId[source.Id] = listener;
                session._listeners.Add(listener);
                listener.Start(AsListener(source, variables), executorFor(source.Kind)!, variables, session._stop.Token);
            }
            session._checks.Add((expectation, listener, null));
        }

        await Task.WhenAll(session._listeners.Select(l => l.WaitUntilReadyAsync(ReadyTimeout, cancellationToken))).ConfigureAwait(false);
        return session;
    }

    /// <summary>A resolved copy of the listener that only subscribes (a broker request in publish mode is switched to subscribe).</summary>
    private static ApiRequest AsListener(ApiRequest source, VariableContext variables)
    {
        var listener = RequestResolver.Resolve(source, variables.Merged());
        listener.Protocol.Mqtt.Mode = MessagingMode.Subscribe;
        listener.Protocol.Kafka.Mode = MessagingMode.Subscribe;
        listener.Protocol.Amqp.Mode = MessagingMode.Subscribe;
        listener.Protocol.Stream.MaxMessages = 0;
        listener.Protocol.Mqtt.MaxMessages = 0;
        listener.Protocol.Kafka.MaxMessages = 0;
        listener.Protocol.Amqp.MaxMessages = 0;
        return listener;
    }

    /// <summary>
    /// Checks every expectation, waiting up to its time limit (measured from <see cref="TriggeredAt"/>) for messages to
    /// arrive. All expectations wait in parallel, so the total wait is the longest single time limit.
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> EvaluateAsync(ApiResponse trigger, Func<string, string> resolve, CancellationToken ct)
    {
        var tasks = _checks.Select(c => EvaluateAsync(c.Expectation, c.Listener, c.Problem, trigger, resolve, ct));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<TestResult> EvaluateAsync(MessageExpectation expectation, Listener? listener, string? problem, ApiResponse trigger,
        Func<string, string> resolve, CancellationToken ct)
    {
        var name = Describe(expectation, listener?.Name ?? "?", resolve);
        if (problem is not null)
            return new TestResult(name, false, problem);
        if (!listener!.Ready && listener.Finished)
            return new TestResult(name, false, $"Listener \"{listener.Name}\" could not subscribe: {listener.Failure ?? "it closed immediately"}");
        if (!trigger.HasResponse)
            return new TestResult(name, false, $"The request itself failed ({trigger.Error}), so no message was checked.");

        var deadline = TriggeredAt + TimeSpan.FromMilliseconds(Math.Max(0, expectation.TimeoutMs));
        var path = resolve(expectation.Path);
        var expected = resolve(expectation.Expected);
        var minCount = Math.Max(1, expectation.MinCount);
        var notConfirmed = listener.Ready ? "" : $" Listener \"{listener.Name}\" never confirmed its subscription, so early messages may have been missed.";

        while (true)
        {
            var (matches, seen, nearest) = Scan(listener, expectation, path, expected);
            var now = DateTimeOffset.Now;
            if (expectation.ExpectNone)
            {
                if (matches.Count > 0)
                    return new TestResult(name, false,
                        $"A matching message arrived after {Ms(matches[0].Timestamp - TriggeredAt)} ms; none was expected.", Truncate(matches[0].Content));
                if (now >= deadline || listener.Finished)
                    return new TestResult(name, true, null, $"no matching message in {expectation.TimeoutMs} ms ({seen} other message(s))");
            }
            else
            {
                if (matches.Count >= minCount)
                    return new TestResult(name, true, null,
                        $"after {Ms(matches[minCount - 1].Timestamp - TriggeredAt)} ms: {Truncate(matches[minCount - 1].Content)}");
                if (now >= deadline || listener.Finished)
                {
                    var why = listener.Finished && now < deadline ? $"the listener closed early ({listener.Failure ?? "connection ended"})" : $"within {expectation.TimeoutMs} ms";
                    var detail = matches.Count > 0
                        ? $"Only {matches.Count} of {minCount} matching message(s) arrived {why}."
                        : $"No matching message arrived {why}.";
                    detail += seen == 0 ? " No messages were received at all." : $" {seen} other message(s) received.";
                    if (nearest is not null)
                        detail += $" Last one: {Truncate(nearest)}";
                    return new TestResult(name, false, detail + notConfirmed, nearest is null ? null : Truncate(nearest));
                }
            }
            try
            {
                await listener.WaitForMessageAsync(deadline - now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new TestResult(name, false, "Cancelled before the message check finished.");
            }
        }
    }

    /// <summary>Messages received after the trigger that pass the channel filter and the value check.</summary>
    private (List<StreamMessage> Matches, int Seen, string? Last) Scan(Listener listener, MessageExpectation expectation, string path, string expected)
    {
        var matches = new List<StreamMessage>();
        var seen = 0;
        string? last = null;
        foreach (var message in listener.Snapshot())
        {
            if (message.Timestamp < TriggeredAt)
                continue;
            if (expectation.Channel.Length > 0 && !(message.Label ?? "").Contains(expectation.Channel, StringComparison.OrdinalIgnoreCase))
            {
                seen++;
                last = message.Content;
                continue;
            }
            if (Matches(message.Content, expectation, path, expected))
                matches.Add(message);
            else
            {
                seen++;
                last = message.Content;
            }
        }
        return (matches, seen, last);
    }

    internal static bool Matches(string content, MessageExpectation expectation, string path, string expected)
    {
        try
        {
            var source = expectation.Source;
            if (source == ValueSource.JsonPath && path.Trim().Length == 0)
                source = ValueSource.Body;
            var values = ResponseValues.Read(new ApiResponse { Body = content, StatusCode = 200 }, source, path);
            return AssertionEvaluator.Check(values, expectation.Operator, expected).Passed;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or RegexMatchTimeoutException
                                       or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public static string Describe(MessageExpectation e, string listenerName, Func<string, string>? resolve = null)
    {
        resolve ??= s => s;
        var subject = e.Source == ValueSource.Body || e.Path.Trim().Length == 0 ? "message" : $"{e.Path}";
        var condition = AssertionEvaluator.Describe(new Assertion
        {
            Source = ValueSource.Body, Operator = e.Operator, Expected = resolve(e.Expected)
        }).Replace("Body", subject, StringComparison.Ordinal);
        var channel = e.Channel.Length > 0 ? $" [{e.Channel}]" : "";
        var count = !e.ExpectNone && e.MinCount > 1 ? $"{e.MinCount}× " : "";
        return e.ExpectNone
            ? $"No message on {listenerName}{channel} where {condition} within {e.TimeoutMs} ms"
            : $"{count}Message on {listenerName}{channel} where {condition} within {e.TimeoutMs} ms";
    }

    private static long Ms(TimeSpan t) => (long)Math.Max(0, t.TotalMilliseconds);

    private static string Truncate(string s)
    {
        var single = s.ReplaceLineEndings(" ");
        return single.Length > 200 ? single[..197] + "..." : single;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var listener in _listeners)
            await listener.StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>One open streaming session collecting received messages.</summary>
    private sealed class Listener(string name) : IProgress<StreamMessage>
    {
        private readonly Lock _gate = new();
        private readonly List<StreamMessage> _received = [];
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<ApiResponse>? _run;

        public string Name { get; } = name;
        public bool Ready => _ready.Task.IsCompleted;
        public bool Finished => _run?.IsCompleted == true;
        public string? Failure { get; private set; }

        public void Start(ApiRequest request, IProtocolExecutor executor, VariableContext variables, CancellationToken stop)
        {
            var context = new ExecutionContext
            {
                Variables = variables,
                Progress = this,
                Interactive = true, // stay open until the check is done
                Listening = () => _ready.TrySetResult()
            };
            _run = Task.Run(async () =>
            {
                try
                {
                    var response = await executor.ExecuteAsync(request, context, stop).ConfigureAwait(false);
                    if (!stop.IsCancellationRequested && !response.IsSuccess)
                        Failure = response.Error ?? response.ReasonPhrase;
                    return response;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return new ApiResponse();
                }
                catch (Exception ex)
                {
                    Failure = ex.Message;
                    return ApiResponse.Failed(ex.Message, TimeSpan.Zero);
                }
                finally
                {
                    Signal();
                }
            }, CancellationToken.None);
        }

        /// <summary>Completes when subscribed, when the listener ends (e.g. could not connect), or after the timeout.</summary>
        public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timer = Task.Delay(timeout, delay.Token);
            await Task.WhenAny(_ready.Task, _run!, timer).ConfigureAwait(false);
            delay.Cancel();
            ct.ThrowIfCancellationRequested();
        }

        public void Report(StreamMessage value)
        {
            if (value.Direction == MessageDirection.Error)
                Failure ??= value.Content;
            if (value.Direction != MessageDirection.Received)
                return;
            lock (_gate)
                _received.Add(value);
            Signal();
        }

        private void Signal()
        {
            TaskCompletionSource arrived;
            lock (_gate)
            {
                arrived = _arrived;
                _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            arrived.TrySetResult();
        }

        public List<StreamMessage> Snapshot()
        {
            lock (_gate)
                return _received.ToList();
        }

        /// <summary>Waits until a new message arrives (or the listener ends), at most <paramref name="timeout"/>.</summary>
        public async Task WaitForMessageAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (timeout <= TimeSpan.Zero)
                return;
            Task arrived;
            lock (_gate)
                arrived = _arrived.Task;
            await Task.WhenAny(arrived, Task.Delay(timeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        public async Task StopAsync()
        {
            if (_run is null)
                return;
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A listener that ignores cancellation must not hang the request.
            }
        }
    }
}
