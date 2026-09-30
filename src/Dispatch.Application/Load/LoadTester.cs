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

public sealed record LoadRequestStats(string Name, int Count, int Errors, LatencyStats Latency, IReadOnlyDictionary<string, int> Statuses);

/// <summary>One second of the run, for the live chart.</summary>
public sealed record LoadSecond(int Second, int Requests, int Errors, double P95, int ActiveUsers);

public sealed class LoadSnapshot
{
    public TimeSpan Elapsed { get; init; }
    public int ActiveUsers { get; init; }
    public int TotalRequests { get; init; }
    public int Errors { get; init; }
    public double RequestsPerSecond { get; init; }
    public LatencyStats Latency { get; init; } = LatencyStats.From([]);
    public IReadOnlyList<LoadSecond> Timeline { get; init; } = [];
}

public sealed class LoadReport
{
    public TimeSpan Duration { get; init; }
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

    public string ToText() =>
        $"""
         {TotalRequests} requests in {Duration.TotalSeconds:0.0} s with {VirtualUsers} virtual users · {RequestsPerSecond:0.0} req/s · {ErrorRate:P1} errors
         latency ms  min {Latency.Min:0}  avg {Latency.Mean:0}  p50 {Latency.P50:0}  p90 {Latency.P90:0}  p95 {Latency.P95:0}  p99 {Latency.P99:0}  max {Latency.Max:0}
         statuses    {string.Join("  ", Statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}: {s.Value}"))}
         {string.Join(Environment.NewLine, PerRequest.Select(r => $"  {r.Name,-40} {r.Count,7} req  {r.Errors,5} err  p95 {r.Latency.P95,6:0} ms"))}
         """;
}

/// <summary>
/// A simple closed-model load test: N virtual users each loop through a journey of requests (with their own
/// variables, so login tokens chain per user) for a duration, with ramp-up and think time.
/// </summary>
public sealed class LoadTester(IRequestSender sender)
{
    private const int MaxErrorSamples = 20;

    private sealed class Sample(string request, double ms, string status, bool error, int second)
    {
        public string Request { get; } = request;
        public double Ms { get; } = ms;
        public string Status { get; } = status;
        public bool Error { get; } = error;
        public int Second { get; } = second;
    }

    public async Task<LoadReport> RunAsync(LoadOptions options, IProgress<LoadSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        if (options.Requests.Count == 0)
            throw new ArgumentException("Choose at least one request.");
        var users = Math.Max(1, options.VirtualUsers);
        var samples = new List<Sample>(capacity: 4096);
        var errorSamples = new List<string>();
        var gate = new Lock();
        var active = 0;
        var clock = Stopwatch.StartNew();

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

        var tasks = Enumerable.Range(0, users).Select(async user =>
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
                        try
                        {
                            response = await sender.SendAsync(request, new SendOptions
                            {
                                Variables = variables,
                                RecordHistory = false,
                                RunScripts = options.RunChecks
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
                        var error = !response.IsSuccess || failedChecks;
                        var status = response.HasResponse ? response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "error";
                        lock (gate)
                        {
                            samples.Add(new Sample(request.Name, response.Elapsed.TotalMilliseconds, status, error, (int)clock.Elapsed.TotalSeconds));
                            if (error && errorSamples.Count < MaxErrorSamples)
                                errorSamples.Add($"{request.Name}: {DescribeError(response, failedChecks)}");
                        }

                        if (options.ThinkTimeMs > 0)
                        {
                            try
                            {
                                await Task.Delay(options.ThinkTimeMs, stop.Token).ConfigureAwait(false);
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
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        clock.Stop();
        await stop.CancelAsync().ConfigureAwait(false);
        await reporting.ConfigureAwait(false);

        List<Sample> all;
        lock (gate)
            all = samples.ToList();
        return new LoadReport
        {
            Duration = clock.Elapsed,
            VirtualUsers = users,
            TotalRequests = all.Count,
            Errors = all.Count(s => s.Error),
            Latency = LatencyStats.From(all.Select(s => s.Ms).ToList()),
            Statuses = all.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
            PerRequest = all.GroupBy(s => s.Request).Select(g => new LoadRequestStats(g.Key, g.Count(), g.Count(s => s.Error),
                LatencyStats.From(g.Select(s => s.Ms).ToList()), g.GroupBy(s => s.Status).ToDictionary(x => x.Key, x => x.Count()))).ToList(),
            Timeline = Timeline(all, 0),
            SampleErrors = errorSamples
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
                Timeline = Timeline(copy, Volatile.Read(ref active))
            };
        }
    }

    private static string DescribeError(ApiResponse response, bool failedChecks)
    {
        if (response.Error is not null)
            return response.Error;
        if (failedChecks && response.TestResults.FirstOrDefault(t => !t.Passed) is { } failed)
            return $"{failed.Name}: {failed.Message}";
        return $"{response.StatusCode} {response.ReasonPhrase}".Trim();
    }

    private static IReadOnlyList<LoadSecond> Timeline(List<Sample> samples, int activeUsers) =>
        samples.GroupBy(s => s.Second).OrderBy(g => g.Key)
            .Select(g => new LoadSecond(g.Key, g.Count(), g.Count(s => s.Error), LatencyStats.From(g.Select(s => s.Ms).ToList()).P95, activeUsers))
            .ToList();
}
