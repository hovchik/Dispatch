using System.Diagnostics;
using System.Globalization;
using Dispatch.Application.Reporting;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.RateLimits;

public sealed class RateLimitOptions
{
    /// <summary>Hard cap on requests sent during the whole probe.</summary>
    public int MaxRequests { get; init; } = 400;

    /// <summary>Hard cap on the probe's duration (bursts plus waiting for the limit to reset).</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Requests in flight during a burst.</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>How often to retry while waiting for a throttled endpoint to accept requests again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Statuses that mean "throttled". 503 counts too when it carries Retry-After.</summary>
    public IReadOnlySet<int> ThrottleStatuses { get; init; } = new HashSet<int> { 429 };

    public ApiEnvironment? Environment { get; init; }
    public IReadOnlyList<KeyValueItem>? CollectionVariables { get; init; }
}

public enum RateLimitPhase
{
    Baseline,
    Burst,
    Recovery,
    Refill,
    Rate
}

/// <summary>One request sent during the probe. <see cref="AtMs"/> is when its response arrived.</summary>
public sealed record RateLimitSample(double AtMs, RateLimitPhase Phase, int Status, bool Throttled, bool Error,
    int? Remaining, double? RetryAfterSeconds)
{
    /// <summary>When the request was sent (ms since the probe started).</summary>
    public double SentAtMs { get; init; }

    /// <summary>How long the server took to answer, in ms.</summary>
    public double LatencyMs { get; init; }
}

/// <summary>How the limit gives capacity back once it has been used up.</summary>
public enum RefillKind
{
    /// <summary>Never throttled within the budget.</summary>
    NoLimitObserved,

    /// <summary>Throttled, but the probe ran out of time or budget before learning how it refills.</summary>
    Unknown,

    /// <summary>All capacity comes back at once (fixed window).</summary>
    FixedWindow,

    /// <summary>Capacity comes back a little at a time (token bucket or sliding window).</summary>
    Gradual
}

/// <summary>The limit as the server advertises it in response headers.</summary>
public sealed record AdvertisedLimit(int? Limit, int? Remaining, double? ResetSeconds, string? Policy, string HeaderStyle);

public sealed class RateLimitReport
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; set; }
    public string RequestName { get; init; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<RateLimitSample> Samples { get; } = [];
    public int RequestsSent => Samples.Count;

    /// <summary>The probe was cancelled or hit its time / request cap.</summary>
    public bool Stopped { get; set; }
    public string? Error { get; set; }

    public bool Throttled { get; set; }
    public int ThrottleStatus { get; set; }

    /// <summary>Requests accepted from a fresh start before the first throttled response.</summary>
    public int BurstCapacity { get; set; }

    /// <summary>Requests per second achieved during the first burst.</summary>
    public double BurstRate { get; set; }

    /// <summary>From the first throttled response to the first accepted one after it.</summary>
    public TimeSpan? Recovery { get; set; }

    /// <summary>From the first request of the burst to recovery: an upper bound on a fixed window's length.</summary>
    public TimeSpan? WindowEstimate { get; set; }

    /// <summary>Retry-After on the first throttled response.</summary>
    public double? RetryAfterSeconds { get; set; }

    public RefillKind Refill { get; set; } = RefillKind.Unknown;

    /// <summary>Capacity right after recovering (≈ burst capacity for a fixed window, small for gradual refill).</summary>
    public int? CapacityAfterRecovery { get; set; }

    /// <summary>Estimated requests per second the limit gives back, for gradual refill.</summary>
    public double? RefillPerSecond { get; set; }

    /// <summary>The bucket was full again after the measuring wait, so the real refill rate may be higher.</summary>
    public bool RefillIsLowerBound { get; set; }
    public AdvertisedLimit? Advertised { get; set; }

    /// <summary>Rate-limit related headers on the first accepted response, exactly as sent.</summary>
    public IReadOnlyList<ResponseHeader> AcceptedHeaders { get; set; } = [];

    /// <summary>Rate-limit related headers on the first throttled response, exactly as sent.</summary>
    public IReadOnlyList<ResponseHeader> ThrottledHeaders { get; set; } = [];

    /// <summary>Start of the first throttled response's body: often says what is limited (IP, key, plan).</summary>
    public string? ThrottleBody { get; set; }

    /// <summary>Median response time of accepted requests, ms.</summary>
    public double? MedianAcceptedMs { get; set; }

    /// <summary>Median response time of throttled requests, ms.</summary>
    public double? MedianThrottledMs { get; set; }

    /// <summary>Requests per second the endpoint allows over time (window capacity / window, or the refill rate).</summary>
    public double? SustainedPerSecond => Refill switch
    {
        RefillKind.FixedWindow when WindowEstimate is { TotalSeconds: > 0 } w => BurstCapacity / w.TotalSeconds,
        RefillKind.Gradual => RefillPerSecond,
        _ => null
    };

    /// <summary>A client rate that stays clear of the limit: 80% of <see cref="SustainedPerSecond"/>.</summary>
    public double? RecommendedPerSecond => SustainedPerSecond * 0.8;

    public List<ReportInsight> Insights { get; } = [];

    /// <summary>One-line summary, e.g. "≈ 100 requests per 60 s window".</summary>
    public string Summary => Error is not null ? Error
        : !Throttled ? $"No limit hit after {BurstCapacity} requests at {BurstRate:0.#} req/s"
        : Refill switch
        {
            RefillKind.FixedWindow => $"{BurstCapacity} requests per window of about {Seconds(WindowEstimate)} (fixed window)",
            RefillKind.Gradual => $"Burst of {BurstCapacity}, then {(RefillIsLowerBound ? "at least" : "about")} {RefillPerSecond?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"} req/s " +
                                  "(token bucket or sliding window)",
            _ => $"Throttled after {BurstCapacity} requests; refill behaviour not determined"
        };

    /// <summary>Formats a duration for reports: "60 s", "1.5 s", or "?".</summary>
    public static string Seconds(TimeSpan? t) => t is null ? "?" : t.Value.TotalSeconds >= 10
        ? $"{t.Value.TotalSeconds:0} s"
        : $"{t.Value.TotalSeconds:0.#} s";
}

/// <summary>
/// Works out an endpoint's real rate-limit policy by probing it: a burst until the first throttled response (capacity),
/// polling until it accepts requests again (recovery, checked against Retry-After), a second burst to see whether
/// capacity returns all at once (fixed window) or gradually (token bucket / sliding window), and, for gradual refill,
/// a timed wait to measure the refill rate. Rate-limit headers are read along the way and checked against what was
/// observed.
///
/// It deliberately sends a lot of requests to one endpoint: use it on APIs you own or may test, ideally with a
/// read-only request.
/// </summary>
public sealed class RateLimitProber(IRequestSender sender)
{
    public async Task<RateLimitReport> ProbeAsync(ApiRequest request, RateLimitOptions options, IProgress<RateLimitSample>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new RateLimitReport { RequestName = request.Name, Url = request.Url };
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.MaxDuration);
        var ct = budget.Token;
        var sendOptions = new SendOptions
        {
            Environment = options.Environment,
            CollectionVariables = options.CollectionVariables,
            RecordHistory = false,
            CheckExpectations = false,
            RunScripts = false
        };
        var gate = new Lock();
        var reserved = 0;
        var errors = 0;
        var sawThrottle = false;

        async Task<RateLimitSample?> SendAsync(RateLimitPhase phase)
        {
            lock (gate)
            {
                // Reserve a slot before sending, so concurrent workers never overshoot the cap.
                if (reserved >= options.MaxRequests)
                    return null;
                reserved++;
            }
            var sentAt = clock.Elapsed.TotalMilliseconds;
            var response = await sender.SendAsync(request, sendOptions, ct).ConfigureAwait(false);
            var retryAfter = RateLimitHeaders.RetryAfterSeconds(response.Headers);
            var throttled = response.HasResponse && (options.ThrottleStatuses.Contains(response.StatusCode)
                                                     || response.StatusCode == 503 && retryAfter is not null);
            var sample = new RateLimitSample(clock.Elapsed.TotalMilliseconds, phase, response.StatusCode, throttled, !response.HasResponse,
                RateLimitHeaders.Read(response.Headers)?.Remaining, retryAfter)
            { SentAtMs = sentAt, LatencyMs = response.Elapsed.TotalMilliseconds };
            lock (gate)
            {
                report.Samples.Add(sample);
                if (response.HasResponse && throttled && !sawThrottle)
                {
                    sawThrottle = true;
                    report.ThrottledHeaders = RateLimitHeaders.Relevant(response.Headers);
                    report.ThrottleBody = Snippet(response.Body);
                }
                else if (response.HasResponse && !throttled && report.AcceptedHeaders.Count == 0)
                    report.AcceptedHeaders = RateLimitHeaders.Relevant(response.Headers);
                if (sample.Error)
                    errors++;
                if (report.Url.Length == 0 || report.Url.Contains("{{", StringComparison.Ordinal))
                    report.Url = response.EffectiveUrl ?? report.Url;
                report.Advertised ??= RateLimitHeaders.Read(response.Headers);
            }
            progress?.Report(sample);
            return sample;
        }

        /// <summary>Sends with <see cref="RateLimitOptions.Concurrency"/> in flight until throttled (or the cap). Returns accepted count.</summary>
        async Task<(int Accepted, RateLimitSample? FirstThrottle, double FirstAt)> BurstAsync(RateLimitPhase phase, int cap)
        {
            var accepted = 0;
            var sent = 0;
            RateLimitSample? firstThrottle = null;
            var firstAt = clock.Elapsed.TotalMilliseconds;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            async Task Worker()
            {
                while (!stop.IsCancellationRequested)
                {
                    lock (gate)
                    {
                        if (sent >= cap || firstThrottle is not null)
                            return;
                        sent++;
                    }
                    var sample = await SendAsync(phase).ConfigureAwait(false);
                    if (sample is null)
                        return;
                    bool failing;
                    lock (gate)
                    {
                        // Every accepted response was admitted by the server, including ones still in flight when another
                        // request got the first throttled response (no new requests start after that).
                        if (sample.Throttled)
                            firstThrottle ??= sample;
                        else if (!sample.Error)
                            accepted++;
                        failing = sample.Error && errors >= 5 && accepted == 0;
                    }
                    if (failing)
                        throw new InvalidOperationException("The request keeps failing without a response: " +
                                                            "fix the request before probing its rate limit.");
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.Concurrency)).Select(_ => Task.Run(Worker, CancellationToken.None))).ConfigureAwait(false);
            return (accepted, firstThrottle, firstAt);
        }

        /// <summary>Polls until a request is accepted again. Returns that sample, or null on timeout / budget.</summary>
        /// <summary>
        /// Polls until a request is accepted again. Also returns when the last rejected poll was sent: the limit reset
        /// somewhere between that and the accepted one.
        /// </summary>
        async Task<(RateLimitSample? Accepted, double? LastRejectedSentAt)> RecoverAsync(RateLimitPhase phase)
        {
            double? lastRejected = null;
            while (true)
            {
                await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
                var sample = await SendAsync(phase).ConfigureAwait(false);
                if (sample is null)
                    return (null, lastRejected);
                if (!sample.Throttled && !sample.Error)
                    return (sample, lastRejected);
                if (sample.Throttled)
                    lastRejected = sample.SentAtMs;
            }
        }

        try
        {
            // ---- 1. Baseline: does the request work, and what does the server advertise? ----
            var baseline = await SendAsync(RateLimitPhase.Baseline).ConfigureAwait(false);
            if (baseline is null || baseline.Error)
            {
                report.Error = "The request got no response; fix it before probing its rate limit.";
                return Finish(report, clock);
            }
            // The server's window starts when it receives the first request, i.e. after it was sent, not when the
            // response arrived (a cold first connection can take a while).
            var windowStart = baseline.SentAtMs;
            if (baseline.Throttled)
            {
                // Already throttled (a previous run, other clients): wait for a clean start.
                var (clean, _) = await RecoverAsync(RateLimitPhase.Baseline).ConfigureAwait(false);
                if (clean is null)
                {
                    report.Error = "The endpoint was already throttled and did not recover within the time limit.";
                    return Finish(report, clock);
                }
                windowStart = clean.SentAtMs;
            }

            // ---- 2. Burst until throttled ----
            var (accepted, throttle, burstStart) = await BurstAsync(RateLimitPhase.Burst, options.MaxRequests).ConfigureAwait(false);
            report.BurstCapacity = accepted + 1; // the baseline (or the request that ended an earlier throttle) used one too
            var burstSeconds = Math.Max(0.001, ((throttle?.AtMs ?? clock.Elapsed.TotalMilliseconds) - burstStart) / 1000);
            report.BurstRate = accepted / burstSeconds;
            if (throttle is null)
            {
                report.Refill = RefillKind.NoLimitObserved;
                return Finish(report, clock);
            }
            report.Throttled = true;
            report.ThrottleStatus = throttle.Status;
            report.RetryAfterSeconds = throttle.RetryAfterSeconds;

            // ---- 3. Recovery ----
            var (recovered, lastRejected) = await RecoverAsync(RateLimitPhase.Recovery).ConfigureAwait(false);
            if (recovered is null)
            {
                report.Stopped = true;
                return Finish(report, clock);
            }
            // The limit reset between the last rejected retry and the first accepted one; take the middle, measured on
            // send times so slow responses don't stretch it.
            var resetAt = ((lastRejected ?? throttle.SentAtMs) + recovered.SentAtMs) / 2;
            report.Recovery = TimeSpan.FromMilliseconds(Math.Max(0, resetAt - throttle.AtMs));
            report.WindowEstimate = TimeSpan.FromMilliseconds(Math.Max(0, resetAt - windowStart));

            // ---- 4. How much capacity came back? ----
            var (refilled, secondThrottle, _) = await BurstAsync(RateLimitPhase.Refill, report.BurstCapacity + 5).ConfigureAwait(false);
            report.CapacityAfterRecovery = refilled + 1;
            if (secondThrottle is null || report.CapacityAfterRecovery >= Math.Max(2, report.BurstCapacity * 0.75))
            {
                report.Refill = RefillKind.FixedWindow;
                return Finish(report, clock);
            }

            // ---- 5. Gradual: measure the refill rate over a timed wait ----
            report.Refill = RefillKind.Gradual;
            // About one request comes back per recovery time, so wait long enough to regain roughly ¾ of the burst capacity:
            // enough requests that counting them is precise, few enough that the bucket can't fill up (which would cap the
            // count). The rate is measured from the moment the bucket was last empty (the previous first throttled
            // request) to the next one, so the burst's own duration is included.
            // If it still comes back full (recovery looked slower than it is, e.g. on a busy server), halve the wait and
            // measure again; each burst drains the bucket, so the next measurement starts from empty.
            var emptyAt = secondThrottle.SentAtMs;
            var wait = TimeSpan.FromMilliseconds(Math.Clamp(report.Recovery.Value.TotalMilliseconds * report.BurstCapacity * 0.75, 500, 15000));
            for (var attempt = 0; ; attempt++)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
                var (regained, nextThrottle, _) = await BurstAsync(RateLimitPhase.Rate, report.BurstCapacity + 5).ConfigureAwait(false);
                var until = nextThrottle?.SentAtMs ?? clock.Elapsed.TotalMilliseconds;
                report.RefillPerSecond = regained / Math.Max(0.001, (until - emptyAt) / 1000);
                // A full bucket means more could have come back: the rate is at least this.
                report.RefillIsLowerBound = regained >= report.BurstCapacity || nextThrottle is null;
                if (!report.RefillIsLowerBound || nextThrottle is null || attempt == 2 || wait.TotalMilliseconds <= 200)
                    break;
                emptyAt = nextThrottle.SentAtMs;
                wait = TimeSpan.FromMilliseconds(Math.Max(200, wait.TotalMilliseconds / 2));
            }
        }
        catch (OperationCanceledException)
        {
            report.Stopped = true;
        }
        catch (InvalidOperationException ex)
        {
            report.Error = ex.Message;
        }
        return Finish(report, clock);
    }

    private static double? Median(IEnumerable<RateLimitSample> samples)
    {
        var v = samples.Select(s => s.LatencyMs).Order().ToArray();
        return v.Length == 0 ? null : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static string? Snippet(string body)
    {
        var text = body.Trim();
        return text.Length == 0 ? null : text.Length > 300 ? text[..300] + "…" : text;
    }

    private static RateLimitReport Finish(RateLimitReport report, Stopwatch clock)
    {
        report.Duration = clock.Elapsed;
        report.MedianAcceptedMs = Median(report.Samples.Where(s => !s.Throttled && !s.Error));
        report.MedianThrottledMs = Median(report.Samples.Where(s => s.Throttled));
        if (report.Samples.Count(s => s.Phase != RateLimitPhase.Baseline) == 0 && report.Error is null && report.Stopped)
            report.Error = "Stopped before any requests were sent.";
        report.Insights.AddRange(RateLimitInsights.For(report));
        return report;
    }
}
