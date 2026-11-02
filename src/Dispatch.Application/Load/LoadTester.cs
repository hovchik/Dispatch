using System.Diagnostics;
using Dispatch.Application.Requests;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Load;

public sealed class LoadOptions
{
    /// <summary>Each virtual user runs these in order, repeatedly (a user journey).</summary>
    public required IReadOnlyList<ApiRequest> Requests { get; init; }
    public int VirtualUsers { get; init; } = 10;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Users start evenly spread over this period.</summary>
    public TimeSpan RampUp { get; init; } = TimeSpan.Zero;

    /// <summary>Stop each user after this many journeys (0 = run for <see cref="Duration"/>).</summary>
    public int IterationsPerUser { get; init; }
    public int ThinkTimeMs { get; init; }
    public ApiEnvironment? Environment { get; init; }
    public IReadOnlyList<KeyValueItem> CollectionVariables { get; init; } = [];

    /// <summary>Assertions and scripts cost CPU; they can be skipped to measure raw throughput.</summary>
    public bool RunChecks { get; init; } = true;
}

public sealed record LatencyStats(double Min, double Mean, double P50, double P90, double P95, double P99, double Max)
{
    public static LatencyStats From(List<double> samples)
    {
        if (samples.Count == 0)
            return new LatencyStats(0, 0, 0, 0, 0, 0, 0);
        var sorted = samples.Order().ToArray();
        double P(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new LatencyStats(sorted[0], sorted.Average(), P(0.50), P(0.90), P(0.95), P(0.99), sorted[^1]);
    }
}

public sealed record LoadRequestStats(string Name, int Count, int Errors, LatencyStats Latency, IReadOnlyDictionary<string, int> Statuses,
    StreamingStats? Streaming = null);

/// <summary>
/// Streaming requests (WebSocket, SSE, Socket.IO, MQTT, Kafka, AMQP, gRPC streams) in a load test. Each virtual user
/// holds one connection at a time: it connects, listens for the request's listen time, disconnects and reconnects. The
/// request's latency columns then measure connection setup; message latency is the time from the last message sent
/// to the reply, or between consecutive server pushes when nothing was sent.
/// </summary>
public sealed record StreamingStats(
    int Connections,
    int ConnectErrors,
    int Drops,
    int MessagesSent,
    int MessagesReceived,
    double MessagesPerSecond,
    LatencyStats MessageLatency)
{
    public int EstablishedConnections => Connections - ConnectErrors;
}

/// <summary>One second of the run, for the live chart. <paramref name="Messages"/> are stream messages received that second.</summary>
public sealed record LoadSecond(int Second, int Requests, int Errors, double P95, int ActiveUsers, int Messages = 0);

/// <summary>A latency histogram bucket: responses that took at most <see cref="UpperMs"/> (and more than the previous bucket).</summary>
public sealed record LatencyBucket(string Label, double UpperMs, int Count);

public sealed class LoadSnapshot
{
    public TimeSpan Elapsed { get; init; }
    public int ActiveUsers { get; init; }
    public int TotalRequests { get; init; }
    public int Errors { get; init; }
    public double RequestsPerSecond { get; init; }
    public LatencyStats Latency { get; init; } = LatencyStats.From([]);
    public IReadOnlyList<LoadSecond> Timeline { get; init; } = [];

    /// <summary>Connection and message counters; null when no streaming request is part of the journey.</summary>
    public StreamingStats? Streaming { get; init; }
}

public sealed class LoadReport
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; init; }

    /// <summary>The run was cancelled before its planned duration.</summary>
    public bool Stopped { get; init; }
    public TimeSpan PlannedDuration { get; init; }
    public TimeSpan RampUp { get; init; }
    public int ThinkTimeMs { get; init; }
    public long TotalBytes { get; init; }
    public IReadOnlyList<LatencyBucket> Histogram { get; init; } = [];
    public double PeakRequestsPerSecond => Timeline.Count == 0 ? 0 : Timeline.Max(t => t.Requests);
    public int VirtualUsers { get; init; }
    public int TotalRequests { get; init; }
    public int Errors { get; init; }
    public double RequestsPerSecond => Duration.TotalSeconds > 0 ? TotalRequests / Duration.TotalSeconds : 0;
    public double ErrorRate => TotalRequests == 0 ? 0 : (double)Errors / TotalRequests;
    public LatencyStats Latency { get; init; } = LatencyStats.From([]);
    public IReadOnlyList<LoadRequestStats> PerRequest { get; init; } = [];
    public IReadOnlyDictionary<string, int> Statuses { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<LoadSecond> Timeline { get; init; } = [];
    public IReadOnlyList<string> SampleErrors { get; init; } = [];

    /// <summary>Connection and message counters over the whole run; null when no streaming request took part.</summary>
    public StreamingStats? Streaming { get; init; }

    public string ToText() =>
        $"""
         {TotalRequests} requests in {Duration.TotalSeconds:0.0} s with {VirtualUsers} virtual users · {RequestsPerSecond:0.0} req/s · {ErrorRate:P1} errors
         latency ms  min {Latency.Min:0}  avg {Latency.Mean:0}  p50 {Latency.P50:0}  p90 {Latency.P90:0}  p95 {Latency.P95:0}  p99 {Latency.P99:0}  max {Latency.Max:0}
         statuses    {string.Join("  ", Statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}: {s.Value}"))}
         {string.Join(Environment.NewLine, PerRequest.Select(r => $"  {r.Name,-40} {r.Count,7} req  {r.Errors,5} err  p95 {r.Latency.P95,6:0} ms"))}
         """ + (Streaming is null ? "" : Environment.NewLine + StreamingText(Streaming));

    public static string StreamingText(StreamingStats s) =>
        $"streaming    {s.Connections} connection(s), {s.ConnectErrors} connect error(s), {s.Drops} drop(s) · {s.MessagesReceived} message(s) received, " +
        $"{s.MessagesSent} sent · {s.MessagesPerSecond:0.0} msg/s · message latency p50 {s.MessageLatency.P50:0} ms  p95 {s.MessageLatency.P95:0} ms  max {s.MessageLatency.Max:0} ms";
}

/// <summary>
/// A simple closed-model load test: N virtual users each loop through a journey of requests (with their own
/// variables, so login tokens chain per user) for a duration, with ramp-up and think time.
/// </summary>
public sealed class LoadTester(IRequestSender sender)
{
    private const int MaxErrorSamples = 20;
    private static readonly TimeSpan InstantFailure = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan InstantFailureBackoff = TimeSpan.FromMilliseconds(100);

    private static readonly double[] BucketBounds = [10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    private sealed class Sample(string request, double ms, string status, bool error, int second, long bytes = 0, StreamSample? stream = null)
    {
        public long Bytes { get; } = bytes;
        public string Request { get; } = request;
        public double Ms { get; } = ms;
        public string Status { get; } = status;
        public bool Error { get; } = error;
        public int Second { get; } = second;

        /// <summary>Set for streaming sessions (one connection each).</summary>
        public StreamSample? Stream { get; } = stream;
    }

    /// <summary>What one streaming session (connection) did, reduced to counters.</summary>
    private sealed record StreamSample(bool ConnectError, bool Dropped, int Sent, int Received, double[] MessageLatencies, int[] ReceivedSeconds);

    /// <summary>Protocols whose request is a session rather than a call, and therefore load tested as connections.</summary>
    public static bool IsStreaming(RequestKind kind) =>
        kind is RequestKind.WebSocket or RequestKind.Sse or RequestKind.SocketIo or RequestKind.Mqtt or RequestKind.Kafka or RequestKind.Amqp;

    public async Task<LoadReport> RunAsync(LoadOptions options, IProgress<LoadSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        if (options.Requests.Count == 0)
            throw new ArgumentException("Choose at least one request.");
        var users = Math.Max(1, options.VirtualUsers);
        var samples = new List<Sample>(capacity: 4096);
        // Distinct error messages with how often each occurred, in first-seen order.
        var errorSamples = new Dictionary<string, int>(StringComparer.Ordinal);
        var errorOrder = new List<string>();
        var gate = new Lock();
        var active = 0;
        var clock = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.IterationsPerUser == 0)
            stop.CancelAfter(options.Duration + options.RampUp);

        using var reporter = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var reporting = Task.Run(async () =>
        {
            try
            {
                while (await reporter.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                    progress?.Report(Snapshot());
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        // Each virtual user runs on the thread pool. Started inline, a user whose requests complete synchronously (an
        // invalid URL, an unresolved {{variable}}) would never yield and would hold the caller's thread, the UI thread
        // in the app, for the whole test.
        var tasks = Enumerable.Range(0, users).Select(user => Task.Run(async () =>
        {
            var delay = options.RampUp > TimeSpan.Zero ? options.RampUp * user / users : TimeSpan.Zero;
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Increment(ref active);
            var variables = VariableContext.For(options.Environment, options.CollectionVariables);
            try
            {
                for (var iteration = 0; !stop.IsCancellationRequested
                                        && (options.IterationsPerUser == 0 || iteration < options.IterationsPerUser); iteration++)
                {
                    variables.Iteration = iteration;
                    foreach (var request in options.Requests)
                    {
                        if (stop.IsCancellationRequested)
                            break;
                        ApiResponse response;
                        var sentAt = DateTimeOffset.Now;
                        var sentAtSecond = clock.Elapsed;
                        try
                        {
                            response = await sender.SendAsync(request, new SendOptions
                            {
                                Variables = variables,
                                RecordHistory = false,
                                RunScripts = options.RunChecks,
                                CheckExpectations = false
                            }, stop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        // A request cut off by the end of the test isn't a real error.
                        if (stop.IsCancellationRequested && !response.HasResponse)
                            break;

                        var failedChecks = options.RunChecks && response.TestResults.Any(t => !t.Passed);
                        var stream = IsStreaming(request.Kind) || response.Messages.Count > 0 ? Streamed(response, sentAt, sentAtSecond) : null;
                        var error = !response.IsSuccess || failedChecks || stream is { Dropped: true };
                        var status = !response.HasResponse ? "error"
                            : stream is not null ? (stream.Dropped ? "dropped" : "connected")
                            : response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        // A session's elapsed time is its listen window; what varies with load is how long connecting took.
                        var ms = stream is null ? response.Elapsed.TotalMilliseconds : ConnectMilliseconds(response, sentAt);
                        lock (gate)
                        {
                            samples.Add(new Sample(request.Name, ms, status, error, (int)(stream is null ? clock.Elapsed : sentAtSecond).TotalSeconds,
                                response.SizeBytes, stream));
                            if (error)
                            {
                                var message = $"{request.Name}: {DescribeError(response, failedChecks, stream)}";
                                if (errorSamples.TryGetValue(message, out var count))
                                    errorSamples[message] = count + 1;
                                else if (errorOrder.Count < MaxErrorSamples)
                                {
                                    errorSamples[message] = 1;
                                    errorOrder.Add(message);
                                }
                            }
                        }

                        // A request that fails without reaching the network fails the same way every time; pause instead
                        // of spinning a core and filling the report with millions of identical errors.
                        var pause = options.ThinkTimeMs > 0 ? options.ThinkTimeMs
                            : !response.HasResponse && response.Elapsed < InstantFailure ? (int)InstantFailureBackoff.TotalMilliseconds : 0;
                        if (pause > 0)
                        {
                            try
                            {
                                await Task.Delay(pause, stop.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, CancellationToken.None)).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        clock.Stop();
        await stop.CancelAsync().ConfigureAwait(false);
        await reporting.ConfigureAwait(false);

        List<Sample> all;
        lock (gate)
            all = samples.ToList();
        return new LoadReport
        {
            StartedAt = startedAt,
            Duration = clock.Elapsed,
            Stopped = cancellationToken.IsCancellationRequested,
            PlannedDuration = options.Duration + options.RampUp,
            RampUp = options.RampUp,
            ThinkTimeMs = options.ThinkTimeMs,
            TotalBytes = all.Sum(s => s.Bytes),
            Histogram = Histogram(all),
            VirtualUsers = users,
            TotalRequests = all.Count,
            Errors = all.Count(s => s.Error),
            Latency = LatencyStats.From(all.Select(s => s.Ms).ToList()),
            Statuses = all.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
            PerRequest = all.GroupBy(s => s.Request).Select(g => new LoadRequestStats(g.Key, g.Count(), g.Count(s => s.Error),
                LatencyStats.From(g.Select(s => s.Ms).ToList()), g.GroupBy(s => s.Status).ToDictionary(x => x.Key, x => x.Count()),
                Streaming(g.ToList(), clock.Elapsed))).ToList(),
            Timeline = Timeline(all, 0),
            SampleErrors = errorOrder.Select(m => errorSamples[m] == 1 ? m : $"{m} (×{errorSamples[m]})").ToList(),
            Streaming = Streaming(all, clock.Elapsed)
        };

        LoadSnapshot Snapshot()
        {
            List<Sample> copy;
            lock (gate)
                copy = samples.ToList();
            var elapsed = clock.Elapsed;
            return new LoadSnapshot
            {
                Elapsed = elapsed,
                ActiveUsers = Volatile.Read(ref active),
                TotalRequests = copy.Count,
                Errors = copy.Count(s => s.Error),
                RequestsPerSecond = elapsed.TotalSeconds > 0 ? copy.Count / elapsed.TotalSeconds : 0,
                Latency = LatencyStats.From(copy.Select(s => s.Ms).ToList()),
                Timeline = Timeline(copy, Volatile.Read(ref active)),
                Streaming = Streaming(copy, elapsed)
            };
        }
    }

    /// <summary>Counters of a streaming session from the messages the executor logged.</summary>
    private static StreamSample Streamed(ApiResponse response, DateTimeOffset sentAt, TimeSpan sentAtSecond)
    {
        if (!response.HasResponse)
            return new StreamSample(true, false, 0, 0, [], []);
        var sent = 0;
        var received = 0;
        var latencies = new List<double>();
        var seconds = new List<int>();
        DateTimeOffset? pendingSent = null;
        var previous = sentAt;
        var dropped = !response.IsSuccess;
        foreach (var message in response.Messages)
        {
            switch (message.Direction)
            {
                case MessageDirection.Sent:
                    sent++;
                    pendingSent = message.Timestamp;
                    break;
                case MessageDirection.Received:
                    received++;
                    var since = pendingSent ?? previous;
                    latencies.Add(Math.Max(0, (message.Timestamp - since).TotalMilliseconds));
                    seconds.Add((int)(sentAtSecond + (message.Timestamp - sentAt)).TotalSeconds);
                    pendingSent = null;
                    previous = message.Timestamp;
                    break;
                case MessageDirection.Error:
                    dropped = true;
                    break;
                case MessageDirection.Info when EndedByServer(message.Content):
                    dropped = true;
                    break;
            }
        }
        return new StreamSample(false, dropped, sent, received, latencies.ToArray(), seconds.ToArray());
    }

    /// <summary>
    /// The executors log one of these when the server, not the listen window or the message limit, ended the session
    /// (WebSocket and Socket.IO close, SSE stream end, Socket.IO disconnect).
    /// </summary>
    private static readonly string[] ServerEndedMessages =
        ["Server closed the connection", "Stream ended by the server", "Server closed the session", "Disconnected by the server"];

    private static bool EndedByServer(string info) => ServerEndedMessages.Any(m => info.StartsWith(m, StringComparison.Ordinal));

    /// <summary>Time until the session was established: the first logged message, else the whole call.</summary>
    private static double ConnectMilliseconds(ApiResponse response, DateTimeOffset sentAt)
    {
        var first = response.Messages.FirstOrDefault();
        return first is null ? response.Elapsed.TotalMilliseconds : Math.Max(0, (first.Timestamp - sentAt).TotalMilliseconds);
    }

    private static StreamingStats? Streaming(List<Sample> samples, TimeSpan elapsed)
    {
        var streams = samples.Where(s => s.Stream is not null).Select(s => s.Stream!).ToList();
        if (streams.Count == 0)
            return null;
        var received = streams.Sum(s => s.Received);
        return new StreamingStats(
            streams.Count,
            streams.Count(s => s.ConnectError),
            streams.Count(s => s.Dropped),
            streams.Sum(s => s.Sent),
            received,
            elapsed.TotalSeconds > 0 ? received / elapsed.TotalSeconds : 0,
            LatencyStats.From(streams.SelectMany(s => s.MessageLatencies).ToList()));
    }

    private static string DescribeError(ApiResponse response, bool failedChecks, StreamSample? stream = null)
    {
        if (response.Error is not null)
            return response.Error;
        if (stream is { Dropped: true })
            return response.Messages.LastOrDefault(m => m.Direction == MessageDirection.Error)?.Content
                   ?? response.Messages.LastOrDefault(m => m.Direction == MessageDirection.Info && EndedByServer(m.Content))?.Content
                   ?? "connection dropped";
        if (failedChecks && response.TestResults.FirstOrDefault(t => !t.Passed) is { } failed)
            return $"{failed.Name}: {failed.Message}";
        return $"{response.StatusCode} {response.ReasonPhrase}".Trim();
    }

    private static IReadOnlyList<LatencyBucket> Histogram(List<Sample> samples)
    {
        var buckets = new List<LatencyBucket>();
        var lower = 0.0;
        foreach (var bound in BucketBounds)
        {
            var count = samples.Count(s => s.Ms <= bound && (lower == 0 || s.Ms > lower));
            buckets.Add(new LatencyBucket(bound >= 1000 ? $"≤ {bound / 1000:0.#} s" : $"≤ {bound:0} ms", bound, count));
            lower = bound;
        }
        buckets.Add(new LatencyBucket($"> {lower / 1000:0.#} s", double.PositiveInfinity, samples.Count(s => s.Ms > lower)));
        return buckets;
    }

    private static IReadOnlyList<LoadSecond> Timeline(List<Sample> samples, int activeUsers)
    {
        var messages = new Dictionary<int, int>();
        foreach (var second in samples.Where(s => s.Stream is not null).SelectMany(s => s.Stream!.ReceivedSeconds))
            messages[second] = messages.GetValueOrDefault(second) + 1;
        var seconds = samples.Select(s => s.Second).Concat(messages.Keys).Distinct().Order();
        var bySecond = samples.GroupBy(s => s.Second).ToDictionary(g => g.Key, g => g.ToList());
        return seconds.Select(second =>
        {
            var inSecond = bySecond.GetValueOrDefault(second) ?? [];
            return new LoadSecond(second, inSecond.Count, inSecond.Count(s => s.Error), LatencyStats.From(inSecond.Select(s => s.Ms).ToList()).P95,
                activeUsers, messages.GetValueOrDefault(second));
        }).ToList();
    }
}
