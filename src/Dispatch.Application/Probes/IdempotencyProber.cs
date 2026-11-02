using System.Text.Json.Nodes;
using Dispatch.Application.Reporting;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Probes;

public sealed class IdempotencyOptions : ProbeOptions
{
    /// <summary>Header that carries the idempotency key.</summary>
    public string KeyHeader { get; init; } = "Idempotency-Key";

    /// <summary>Send the request twice with the same key.</summary>
    public bool Replay { get; init; } = true;

    /// <summary>Reuse a key with a different body.</summary>
    public bool KeyReuse { get; init; } = true;

    /// <summary>Identical requests sent at the same moment with one key.</summary>
    public int Parallel { get; init; } = 5;

    /// <summary>Also race identical requests without a key (double submits, double spends).</summary>
    public bool RaceWithoutKey { get; init; } = true;
}

/// <summary>
/// Checks that a mutating request is safe to retry: replays it with the same Idempotency-Key, reuses the key with a
/// different body, and sends identical requests at the same moment (with and without a key), then compares the
/// resources created and the responses returned. PUT and DELETE are also checked against HTTP's own idempotency rules.
///
/// It creates data on purpose: run it against a test environment.
/// </summary>
public sealed class IdempotencyProber(IRequestSender sender)
{
    private sealed record Outcome(ApiResponse Response, ProbeStep Step)
    {
        public bool Ok => Response.HasResponse && Response.StatusCode is >= 200 and < 300;
        public string? Resource => Ok ? ProbeHelpers.ResourceId(Response) : null;
        public string Body => ProbeHelpers.Canonical(Response.Body);
    }

    public async Task<ProbeReport> ProbeAsync(ApiRequest request, IdempotencyOptions options, IProgress<ProbeStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ProbeReport
        {
            Kind = ProbeKind.Idempotency, RequestName = request.Name, Method = request.Method.ToString().ToUpperInvariant(), Url = request.Url
        };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.MaxDuration);
        var session = new ProbeSession(sender, report, options, progress, budget.Token);
        try
        {
            await ProbeCoreAsync(request, options, report, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Stopped = true;
        }
        session.Finish();
        return report;
    }

    private static string NewKey() => "dispatch-" + Guid.NewGuid().ToString("N");

    private static async Task<Outcome?> SendAsync(ProbeSession session, ApiRequest request, string label)
    {
        var sent = await session.SendAsync(request, label, r =>
        {
            var id = r.HasResponse && r.StatusCode is >= 200 and < 300 ? ProbeHelpers.ResourceId(r) : null;
            var replayed = ProbeHelpers.Header(r, "Idempotent-Replayed") ?? ProbeHelpers.Header(r, "Idempotency-Replayed") ?? ProbeHelpers.Header(r, "X-Idempotent-Replayed");
            var note = string.Join(", ", new[] { id is null ? null : $"resource {id}", replayed is null ? null : $"replayed: {replayed}" }.Where(x => x is not null));
            return (note.Length == 0 ? null : note, r.StatusCode >= 500);
        }).ConfigureAwait(false);
        return sent is { } s ? new Outcome(s.Response, s.Step) : null;
    }

    /// <summary>A body that differs from the original: a JSON member changed (or added), or text appended.</summary>
    public static ApiRequest WithDifferentBody(ApiRequest request)
    {
        var copy = request.Clone();
        var json = ProbeHelpers.TryJson(copy.Body.Content);
        if (json is JsonObject o)
        {
            var target = o.FirstOrDefault(p => p.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number);
            if (target.Key is not null && o[target.Key] is JsonValue num && num.TryGetValue<double>(out var d))
                o[target.Key] = d + 1;
            else if (o.FirstOrDefault(p => p.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String) is { Key: not null } str)
                o[str.Key] = JsonPath(o[str.Key]) + "-changed";
            else
                o["dispatchProbe"] = "changed";
            copy.Body.Content = o.ToJsonString();
        }
        else if (copy.Body.Mode is BodyMode.FormUrlEncoded or BodyMode.Multipart)
            copy.Body.FormFields.Add(new KeyValueItem("dispatchProbe", "changed"));
        else
            copy.Body.Content += copy.Body.Content.Length == 0 ? "dispatch-probe=changed" : " ";
        if (copy.Body.Mode == BodyMode.None)
            copy.Body.Mode = BodyMode.Json;
        return copy;

        static string JsonPath(JsonNode? node) => Testing.JsonPath.ToText(node);
    }

    private static async Task<List<Outcome>> RaceAsync(ProbeSession session, ApiRequest request, int count, string label)
    {
        // Start every request at once: each task waits on the same gate.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(1, count).Select(i => Task.Run(async () =>
        {
            await gate.Task.ConfigureAwait(false);
            return await SendAsync(session, request, $"{label} #{i}").ConfigureAwait(false);
        })).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    private static string Statuses(IEnumerable<Outcome> outcomes) =>
        string.Join(", ", outcomes.GroupBy(o => o.Step.StatusText).OrderBy(g => g.Key).Select(g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key));

    private static async Task ProbeCoreAsync(ApiRequest request, IdempotencyOptions options, ProbeReport report, ProbeSession session)
    {
        var method = request.Method;
        var header = options.KeyHeader.Trim();
        var mutating = method is HttpVerb.Post or HttpVerb.Put or HttpVerb.Patch or HttpVerb.Delete;
        if (!mutating)
            report.Insights.Add(new ReportInsight(InsightLevel.Info,
                $"{method.ToString().ToUpperInvariant()} is safe by definition; idempotency matters for POST, PATCH, PUT and DELETE."));

        // ---- 1. Replay with the same key ----
        var key = NewKey();
        var keyed = ProbeHelpers.WithHeader(request, header, key);
        var original = await SendAsync(session, keyed, $"original ({header})").ConfigureAwait(false);
        if (original is null)
            return;
        if (!original.Response.HasResponse)
        {
            report.Error = $"The request got no response ({original.Response.Error}); fix it before probing.";
            return;
        }
        if (original.Response.StatusCode >= 400)
        {
            report.Error = $"The request answered HTTP {original.Response.StatusCode}; it needs to succeed before its retries can be judged.";
            return;
        }

        string replayVerdict = "—";
        var keyHonoured = false;
        if (options.Replay)
        {
            var replay = await SendAsync(session, keyed, "replay, same key").ConfigureAwait(false);
            if (replay is not null)
            {
                var replayedHeader = ProbeHelpers.Header(replay.Response, "Idempotent-Replayed") ?? ProbeHelpers.Header(replay.Response, "Idempotency-Replayed")
                                     ?? ProbeHelpers.Header(replay.Response, "X-Idempotent-Replayed");
                if (replay.Response.StatusCode >= 500 || !replay.Response.HasResponse)
                {
                    replayVerdict = "crashed";
                    report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                        $"Retrying with the same {header} failed with {replay.Step.StatusText}: a client retrying after a timeout gets an error for a request that succeeded."));
                }
                else if (replay.Ok && original.Resource is { } a && replay.Resource is { } b && a != b)
                {
                    replayVerdict = "duplicate";
                    report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                        $"Retrying with the same {header} created a second resource ({a} then {b}). The key is ignored, so a client retry after a timeout " +
                        "creates duplicates (double orders, double charges)."));
                }
                else if (replay.Ok && replay.Response.StatusCode == original.Response.StatusCode && replay.Body == original.Body)
                {
                    replayVerdict = "same response";
                    // Only a replay marker, or a POST that didn't create a second resource, shows the key is used. PUT, PATCH and
                    // DELETE often return the same response without looking at the key at all.
                    keyHonoured = replayedHeader is not null || method == HttpVerb.Post && original.Resource is not null;
                    report.Insights.Add(new ReportInsight(InsightLevel.Good,
                        $"Retrying with the same {header} returned the original response (HTTP {replay.Response.StatusCode})" +
                        (replayedHeader is null ? "." : $" and marked it as replayed ({replayedHeader}).")));
                }
                else if (replay.Response.StatusCode == 409)
                {
                    replayVerdict = "409";
                    keyHonoured = true;
                    report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                        $"Retrying with the same {header} after the first request completed is rejected with 409. That prevents duplicates, but a client " +
                        "that retried after a lost response can't learn the result; return the original response instead (409 is meant for a request still in progress)."));
                }
                else if (replay.Ok)
                {
                    replayVerdict = "different response";
                    report.Insights.Add(new ReportInsight(method == HttpVerb.Post && original.Resource is null ? InsightLevel.Warning : InsightLevel.Info,
                        $"Retrying with the same {header} returned a different response (HTTP {original.Response.StatusCode} then {replay.Response.StatusCode})" +
                        (original.Resource is null ? "; no resource id was found to tell whether a duplicate was created." : " for the same resource.")));
                }
                else
                {
                    replayVerdict = $"HTTP {replay.Response.StatusCode}";
                    report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                        $"Retrying with the same {header} answered HTTP {replay.Response.StatusCode}, while the original succeeded."));
                }
            }
        }

        // ---- 2. Same key, different body ----
        string reuseVerdict = "—";
        if (options.KeyReuse && method != HttpVerb.Delete && request.Body.Mode == BodyMode.Binary)
            report.Insights.Add(new ReportInsight(InsightLevel.Info,
                "The key-reuse check was skipped: the body is a binary file, and the probe can't send different bytes."));
        else if (options.KeyReuse && method != HttpVerb.Delete)
        {
            var changed = WithDifferentBody(keyed);
            var reuse = await SendAsync(session, changed, "same key, different body").ConfigureAwait(false);
            if (reuse is not null)
            {
                if (reuse.Response.StatusCode is 422 or 409 or 400)
                {
                    reuseVerdict = $"rejected ({reuse.Response.StatusCode})";
                    report.Insights.Add(new ReportInsight(InsightLevel.Good,
                        $"Reusing the key with a different body is rejected with HTTP {reuse.Response.StatusCode}" +
                        (reuse.Response.StatusCode == 422 ? " (as the IETF Idempotency-Key draft recommends)." : "; the IETF Idempotency-Key draft recommends 422.")));
                }
                else if (reuse.Response.StatusCode >= 500 || !reuse.Response.HasResponse)
                {
                    reuseVerdict = "crashed";
                    report.Insights.Add(new ReportInsight(InsightLevel.Bad, $"Reusing the key with a different body crashed the endpoint ({reuse.Step.StatusText})."));
                }
                else if (reuse.Ok && reuse.Body == original.Body)
                {
                    reuseVerdict = "original returned";
                    report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                        "Reusing the key with a different body silently returned the original response: the client believes its changed request was applied. " +
                        "Reject a reused key with a different payload (422)."));
                }
                else if (reuse.Ok)
                {
                    reuseVerdict = "accepted";
                    report.Insights.Add(new ReportInsight(keyHonoured ? InsightLevel.Bad : InsightLevel.Warning,
                        "Reusing the key with a different body was accepted as a new request" +
                        (reuse.Resource is { } id && id != original.Resource ? $" (created {id})" : "") +
                        ". The server doesn't tie the key to the payload, so a client bug that reuses keys goes unnoticed."));
                }
                else
                    reuseVerdict = $"HTTP {reuse.Response.StatusCode}";
            }
        }

        // ---- 3. Concurrent requests with one key ----
        string raceVerdict = "—";
        if (options.Parallel >= 2)
        {
            var raceKey = ProbeHelpers.WithHeader(request, header, NewKey());
            var race = await RaceAsync(session, raceKey, options.Parallel, "parallel, one key").ConfigureAwait(false);
            var created = race.Where(o => o.Ok).Select(o => o.Resource).Where(id => id is not null).Distinct().ToList();
            var crashed = race.Count(o => !o.Response.HasResponse || o.Response.StatusCode >= 500);
            raceVerdict = $"{Statuses(race)}";
            if (created.Count > 1)
                report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                    $"{race.Count} simultaneous requests with the same {header} created {created.Count} different resources ({string.Join(", ", created.Take(4))}). " +
                    "The key is checked before the first request finishes, not locked: concurrent retries still duplicate."));
            else if (crashed > 0)
                report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                    $"{crashed} of {race.Count} simultaneous requests with one key failed ({Statuses(race)}). Concurrent duplicates should get the original response or a 409."));
            else if (race.Count < 2)
                raceVerdict = "not run (request cap)";
            else if (created.Count == 1 || race.Count(o => o.Ok) <= 1)
                report.Insights.Add(new ReportInsight(InsightLevel.Good,
                    $"{race.Count} simultaneous requests with one key were handled safely ({Statuses(race)})" +
                    (created.Count == 1 ? $": one resource ({created[0]})." : ".")));
            else
                report.Insights.Add(new ReportInsight(InsightLevel.Info,
                    $"{race.Count} simultaneous requests with one key: {Statuses(race)}. No resource id was found to tell whether duplicates were created."));
        }

        // ---- 4. Concurrent requests without a key ----
        string noKeyVerdict = "—";
        if (options.RaceWithoutKey && options.Parallel >= 2)
        {
            var bare = ProbeHelpers.WithHeader(request, header, null);
            var race = await RaceAsync(session, bare, options.Parallel, "parallel, no key").ConfigureAwait(false);
            var created = race.Where(o => o.Ok).Select(o => o.Resource).Where(id => id is not null).Distinct().ToList();
            var crashed = race.Count(o => !o.Response.HasResponse || o.Response.StatusCode >= 500);
            noKeyVerdict = race.Count < 2 ? "not run (request cap)" : Statuses(race);
            if (crashed > 0)
                report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                    $"{crashed} of {race.Count} simultaneous identical requests without a key failed ({Statuses(race)}): the endpoint has a race under concurrency " +
                    "(lock or unique-constraint errors surfacing as 5xx)."));
            if (method == HttpVerb.Post && created.Count > 1)
                report.Insights.Add(new ReportInsight(InsightLevel.Info,
                    $"Without a key, {race.Count} simultaneous identical POSTs created {created.Count} resources. That is normal for POST, but a double-clicked " +
                    $"submit does the same: clients should send {header}."));
            else if (method == HttpVerb.Post && race.Count >= 2 && race.All(o => o.Response.StatusCode is 400 or 422 or 428))
                report.Insights.Add(new ReportInsight(InsightLevel.Good, $"Requests without {header} are rejected ({Statuses(race)}): the key is required."));
            if (method is HttpVerb.Put or HttpVerb.Patch && race.Where(o => o.Ok).Select(o => o.Body).Distinct().Count() > 1)
                report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                    $"Simultaneous identical {method.ToString().ToUpperInvariant()} requests returned different bodies: concurrent writes are not isolated."));
        }

        // ---- 5. HTTP semantics: PUT and DELETE are idempotent by definition ----
        string semanticsVerdict = "—";
        if (method == HttpVerb.Put)
        {
            var again = await SendAsync(session, ProbeHelpers.WithHeader(request, header, null), "PUT again, no key").ConfigureAwait(false);
            if (again is not null)
            {
                semanticsVerdict = again.Step.StatusText;
                if (!again.Ok)
                    report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                        $"Repeating the PUT answered {again.Step.StatusText}. PUT must be idempotent: sending it again should succeed with the same result."));
                else if (again.Body != original.Body)
                    report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                        "Repeating the PUT returned a different body (ignoring timestamps): the result depends on how many times it was sent."));
                else
                    report.Insights.Add(new ReportInsight(InsightLevel.Good, "Repeating the PUT gives the same result."));
            }
        }
        else if (method == HttpVerb.Delete)
        {
            var again = await SendAsync(session, ProbeHelpers.WithHeader(request, header, null), "DELETE again, no key").ConfigureAwait(false);
            if (again is not null)
            {
                semanticsVerdict = again.Step.StatusText;
                if (again.Response.StatusCode >= 500 || !again.Response.HasResponse)
                    report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                        $"Deleting an already deleted resource answered {again.Step.StatusText}. DELETE must be idempotent: expect 404, 410 or 204."));
                else
                    report.Insights.Add(new ReportInsight(InsightLevel.Good, $"Deleting it again answers HTTP {again.Response.StatusCode}, which retries can handle."));
            }
        }

        if (method == HttpVerb.Post && original.Resource is null)
            report.Insights.Add(new ReportInsight(InsightLevel.Info,
                "No resource id was found in the responses (Location header, $.id, $.uuid, …), so duplicate creates can only be judged by the responses."));

        report.Stats.Add(new ProbeStat("Key header", header));
        report.Stats.Add(new ProbeStat("Replay, same key", replayVerdict, null, replayVerdict is "duplicate" or "crashed" ? true : replayVerdict == "same response" ? false : null));
        report.Stats.Add(new ProbeStat("Same key, new body", reuseVerdict, null, reuseVerdict.StartsWith("rejected", StringComparison.Ordinal) ? false
            : reuseVerdict is "accepted" or "crashed" or "original returned" ? true : null));
        report.Stats.Add(new ProbeStat("Parallel, one key", raceVerdict, $"{options.Parallel} at once"));
        report.Stats.Add(new ProbeStat("Parallel, no key", noKeyVerdict));
        if (semanticsVerdict != "—")
            report.Stats.Add(new ProbeStat($"{method.ToString().ToUpperInvariant()} repeated", semanticsVerdict));

        report.Details["keyHeader"] = header;
        report.Details["originalResource"] = original.Resource;
        report.Details["replay"] = replayVerdict;
        report.Details["keyReuse"] = reuseVerdict;
        report.Details["parallelWithKey"] = raceVerdict;
        report.Details["parallelWithoutKey"] = noKeyVerdict;
        report.Details["parallel"] = options.Parallel;

        var problems = report.Insights.Count(i => i.Level == InsightLevel.Bad);
        report.Summary = problems == 0 && report.Stopped
            ? $"Incomplete: stopped after {report.RequestsSent} requests, no problems found so far"
            : problems == 0
            ? $"Safe to retry: {report.RequestsSent} requests, no duplicates or failures found"
            : $"{problems} retry problem(s) found in {report.RequestsSent} requests";
    }
}
