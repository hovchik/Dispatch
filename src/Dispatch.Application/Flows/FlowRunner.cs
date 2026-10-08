using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Flows;

public sealed class FlowRunOptions
{
    public required IReadOnlyList<ApiRequest> Requests { get; init; }
    public ApiEnvironment? Environment { get; init; }
    public IReadOnlyList<KeyValueItem> CollectionVariables { get; init; } = [];
    public string? CollectionSpec { get; init; }
    public IReadOnlyDictionary<string, string>? Globals { get; init; }
    public bool RecordHistory { get; init; }

    /// <summary>Safety cap on total steps executed, so a bad Until can't loop forever.</summary>
    public int MaxSteps { get; init; } = 10_000;

    /// <summary>Replay a recorded run with one response changed, instead of running normally.</summary>
    public FlowFork? Fork { get; init; }
}

public enum FlowEventKind
{
    StepStarted,
    StepFinished,
    Skipped,
    Info,
    Failed
}

/// <summary>
/// A line in the running log of a flow. Request lines carry the request's number in the run (<see cref="Exchange"/>,
/// for forking) and where its response came from.
/// </summary>
public sealed record FlowEvent(FlowEventKind Kind, int Depth, string StepName, string Detail = "", bool Ok = true,
    ApiResponse? Response = null, int? Exchange = null, ExchangeOrigin? Origin = null)
{
    public bool CanFork => Exchange is not null;
    public string ExchangeLabel => Exchange is { } n ? $"#{n}" : "";
    public string OriginLabel => Origin switch
    {
        ExchangeOrigin.Recorded => "replayed",
        ExchangeOrigin.Edited => "edited",
        _ => ""
    };
}

public sealed class FlowResult
{
    public bool Passed { get; set; } = true;
    public bool Stopped { get; set; }
    public string? StopReason { get; set; }
    public TimeSpan Duration { get; set; }
    public int StepsRun { get; set; }
    public int RequestsSent { get; set; }
    public int TestsPassed { get; set; }
    public int TestsFailed { get; set; }
    public List<FlowEvent> Events { get; } = [];
    public Dictionary<string, string> EnvironmentUpdates { get; } = new(StringComparer.Ordinal);

    /// <summary>Every request of the run with its response, for replaying and forking it later.</summary>
    public FlowRecording Recording { get; } = new();

    /// <summary>For a fork replay: whether the run reached the forked request (a run that took another path may not).</summary>
    public bool ForkReached { get; set; }
}

/// <summary>
/// Executes a <see cref="TestFlow"/>: requests, scripts, variable assignments, delays and the control-flow steps
/// (if / repeat / for-each / until / stop), sharing one variable context so a value extracted by one request drives the
/// next. Conditions and loop sources resolve <c>{{variables}}</c> first.
/// </summary>
public sealed class FlowRunner(IRequestSender sender)
{
    private sealed class StopFlow(bool fail, string? reason) : Exception
    {
        public bool Fail { get; } = fail;
        public string? Reason { get; } = reason;
    }

    public async Task<FlowResult> RunAsync(TestFlow flow, FlowRunOptions options, IProgress<FlowEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new FlowResult();
        result.Recording.FlowName = flow.Name;
        result.Recording.FlowId = flow.Id;
        var stopwatch = Stopwatch.StartNew();
        var variables = VariableContext.For(options.Environment, options.CollectionVariables, globals: options.Globals);
        var requests = options.Requests.ToDictionary(r => r.Id);

        try
        {
            await RunStepsAsync(flow.Steps, 0, new State(options, variables, requests, result, progress), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StopFlow stop)
        {
            result.Stopped = true;
            result.StopReason = stop.Reason;
            if (stop.Fail)
                result.Passed = false;
            Emit(progress, result, new FlowEvent(FlowEventKind.Info, 0, "Stop", stop.Reason ?? (stop.Fail ? "failed" : "stopped"), !stop.Fail));
        }
        catch (OperationCanceledException)
        {
            result.Stopped = true;
            result.StopReason = "Cancelled";
        }

        if (options.Fork is not null && !result.ForkReached)
            Emit(progress, result, new FlowEvent(FlowEventKind.Info, 0, "Fork",
                $"request #{options.Fork.ForkOrdinal} was not reached: the replay took a different path"));

        foreach (var (k, v) in variables.EnvironmentUpdates)
            result.EnvironmentUpdates[k] = v;
        result.Duration = stopwatch.Elapsed;
        result.Recording.Passed = result.Passed;
        result.Recording.Steps = FlowRunDiff.Outcomes(result);
        return result;
    }

    private sealed record State(FlowRunOptions Options, VariableContext Variables, IReadOnlyDictionary<Guid, ApiRequest> Requests,
        FlowResult Result, IProgress<FlowEvent>? Progress)
    {
        /// <summary>How often each request step has run so far (loops run a step many times).</summary>
        public Dictionary<Guid, int> Occurrences { get; } = [];
        public int Ordinal { get; set; }

        /// <summary>While replaying the part before the fork (or offline after it), delays are skipped.</summary>
        public bool Replaying => Options.Fork is { } fork && (!Result.ForkReached || fork.After == AfterFork.Recorded);
    }

    private async Task RunStepsAsync(IReadOnlyList<FlowStep> steps, int depth, State state, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!step.Enabled)
            {
                Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Skipped, depth, Label(step, state), "disabled"));
                continue;
            }
            if (++state.Result.StepsRun > state.Options.MaxSteps)
                throw new StopFlow(true, $"Step limit ({state.Options.MaxSteps}) reached — check for a loop that never ends.");
            await RunStepAsync(step, depth, state, ct).ConfigureAwait(false);
        }
    }

    private async Task RunStepAsync(FlowStep step, int depth, State state, CancellationToken ct)
    {
        var name = Label(step, state);
        switch (step.Type)
        {
            case FlowStepType.Group:
                Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Info, depth, name));
                await RunStepsAsync(step.Children, depth + 1, state, ct).ConfigureAwait(false);
                break;

            case FlowStepType.Request:
                await RunRequestAsync(step, depth, state, name, ct).ConfigureAwait(false);
                break;

            case FlowStepType.Script:
                await RunScriptAsync(step, depth, state, name, ct).ConfigureAwait(false);
                break;

            case FlowStepType.SetVariable:
                var value = state.Variables.Resolve(step.Value);
                state.Variables.Set(step.Variable.Trim(), value, step.Scope);
                Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.StepFinished, depth, name, $"{step.Variable} = {Truncate(value)}"));
                break;

            case FlowStepType.Delay:
                Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Info, depth, name,
                    state.Replaying ? $"{step.Milliseconds} ms (skipped in replay)" : $"{step.Milliseconds} ms"));
                if (!state.Replaying)
                    await Task.Delay(Math.Clamp(step.Milliseconds, 0, 600_000), ct).ConfigureAwait(false);
                break;

            case FlowStepType.If:
                var holds = Evaluate(step.Condition, state.Variables);
                Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Info, depth, name, holds ? "true" : "false"));
                if (holds)
                    await RunStepsAsync(step.Children, depth + 1, state, ct).ConfigureAwait(false);
                break;

            case FlowStepType.Repeat:
                var count = Math.Clamp(step.Count, 0, 100_000);
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    state.Variables.Set("$index", i.ToString(), VariableScope.Runtime);
                    await RunStepsAsync(step.Children, depth + 1, state, ct).ConfigureAwait(false);
                }
                break;

            case FlowStepType.ForEach:
                await RunForEachAsync(step, depth, state, name, ct).ConfigureAwait(false);
                break;

            case FlowStepType.Until:
                await RunUntilAsync(step, depth, state, name, ct).ConfigureAwait(false);
                break;

            case FlowStepType.Stop:
                if (step.Condition.Left.Length == 0 || Evaluate(step.Condition, state.Variables))
                    throw new StopFlow(step.Fail, state.Variables.Resolve(step.Value) is { Length: > 0 } m ? m : null);
                break;
        }
    }

    private async Task RunRequestAsync(FlowStep step, int depth, State state, string name, CancellationToken ct)
    {
        if (step.RequestId is not { } id || !state.Requests.TryGetValue(id, out var request))
        {
            Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Failed, depth, name, "request not found", false));
            if (!step.ContinueOnError)
                throw new StopFlow(true, $"{name}: request not found");
            return;
        }

        Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.StepStarted, depth, name));
        var occurrence = state.Occurrences[step.Id] = state.Occurrences.GetValueOrDefault(step.Id) + 1;
        var ordinal = ++state.Ordinal;
        var (origin, served) = Serve(step, occurrence, state);
        var response = await sender.SendAsync(request, new SendOptions
        {
            Variables = state.Variables,
            CollectionSpec = state.Options.CollectionSpec,
            CollectionRequests = state.Options.Requests,
            RecordHistory = state.Options.RecordHistory,
            ResponseOverride = served is null ? null : _ => served
        }, ct).ConfigureAwait(false);
        state.Result.Recording.Exchanges.Add(new RecordedExchange(ordinal, step.Id, occurrence, name, RecordedResponse.From(response)));

        state.Result.RequestsSent++;
        state.Result.TestsPassed += response.TestResults.Count(t => t.Passed);
        state.Result.TestsFailed += response.TestResults.Count(t => !t.Passed);
        var ok = response.HasResponse && response.AllTestsPassed;
        var detail = (origin switch { ExchangeOrigin.Edited => "✎ ", ExchangeOrigin.Recorded => "↺ ", _ => "" }) + (response.HasResponse
            ? $"{response.StatusCode} {response.ReasonPhrase} · {response.Elapsed.TotalMilliseconds:0} ms" +
              (response.TestResults.Count > 0 ? $" · {response.TestResults.Count(t => t.Passed)}/{response.TestResults.Count} tests" : "")
            : $"error: {response.Error}");
        Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.StepFinished, depth, name, detail, ok, response, ordinal, origin));

        if (!ok && !step.ContinueOnError)
            throw new StopFlow(true, $"{name}: {detail}");
    }

    /// <summary>
    /// For a fork replay: whether this request is answered from the recording, with the edited response, or live.
    /// Returns the response to serve (null = send it for real).
    /// </summary>
    private static (ExchangeOrigin Origin, ApiResponse? Served) Serve(FlowStep step, int occurrence, State state)
    {
        if (state.Options.Fork is not { } fork)
            return (ExchangeOrigin.Live, null);
        var target = fork.Recording.Find(fork.ForkOrdinal);
        if (!state.Result.ForkReached && target is not null && target.StepId == step.Id && target.Occurrence == occurrence)
        {
            state.Result.ForkReached = true;
            return fork.Replacement is null ? (ExchangeOrigin.Live, null) : (ExchangeOrigin.Edited, fork.Replacement.ToResponse());
        }
        var recorded = fork.Recording.Find(step.Id, occurrence);
        if (!state.Result.ForkReached)
            // Before the fork: as recorded (a request the original run didn't send goes out live).
            return recorded is null ? (ExchangeOrigin.Live, null) : (ExchangeOrigin.Recorded, recorded.Response.ToResponse());
        if (fork.After == AfterFork.Live)
            return (ExchangeOrigin.Live, null);
        // Offline after the fork; a request the original run never sent has nothing to answer with.
        return recorded is not null
            ? (ExchangeOrigin.Recorded, recorded.Response.ToResponse())
            : (ExchangeOrigin.Recorded, ApiResponse.Failed(
                "Not in the recording: this request didn't run in the original flow (replay with live requests after the fork)", TimeSpan.Zero));
    }

    private async Task RunScriptAsync(FlowStep step, int depth, State state, string name, CancellationToken ct)
    {
        // Reuse a request's pre-request script path: run the snippet with the shared variables via a scratch request.
        var scratch = new ApiRequest { Name = name, PreRequestScript = step.Value };
        var response = await sender.SendWithScriptOnlyAsync(scratch, state.Variables, ct).ConfigureAwait(false);
        var ok = response.Error is null;
        Emit(state.Progress, state.Result, new FlowEvent(ok ? FlowEventKind.StepFinished : FlowEventKind.Failed, depth, name,
            ok ? string.Join(" · ", response.ScriptLog) : response.Error ?? "script error", ok));
        if (!ok && !step.ContinueOnError)
            throw new StopFlow(true, $"{name}: {response.Error}");
    }

    private async Task RunForEachAsync(FlowStep step, int depth, State state, string name, CancellationToken ct)
    {
        var source = state.Variables.Resolve(step.Value).Trim();
        JsonArray? array = null;
        try
        {
            array = JsonNode.Parse(source) as JsonArray;
        }
        catch (JsonException)
        {
        }
        if (array is null)
        {
            Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Failed, depth, name, "value is not a JSON array", false));
            if (!step.ContinueOnError)
                throw new StopFlow(true, $"{name}: not a JSON array");
            return;
        }
        Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Info, depth, name, $"{array.Count} item(s)"));
        for (var i = 0; i < array.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = array[i];
            state.Variables.Set(step.Variable.Trim() is { Length: > 0 } v ? v : "$item",
                item is JsonValue val && val.TryGetValue<string>(out var s) ? s : item?.ToJsonString() ?? "", VariableScope.Runtime);
            state.Variables.Set("$index", i.ToString(), VariableScope.Runtime);
            await RunStepsAsync(step.Children, depth + 1, state, ct).ConfigureAwait(false);
        }
    }

    private async Task RunUntilAsync(FlowStep step, int depth, State state, string name, CancellationToken ct)
    {
        var max = Math.Clamp(step.MaxAttempts, 1, 10_000);
        for (var attempt = 1; attempt <= max; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            state.Variables.Set("$attempt", attempt.ToString(), VariableScope.Runtime);
            await RunStepsAsync(step.Children, depth + 1, state, ct).ConfigureAwait(false);
            if (Evaluate(step.Condition, state.Variables))
            {
                Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Info, depth, name, $"condition met after {attempt} attempt(s)"));
                return;
            }
            if (attempt < max && step.Milliseconds > 0 && !state.Replaying)
                await Task.Delay(Math.Clamp(step.Milliseconds, 0, 600_000), ct).ConfigureAwait(false);
        }
        Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Failed, depth, name, $"condition not met after {max} attempt(s)", false));
        if (!step.ContinueOnError)
            throw new StopFlow(true, $"{name}: condition not met after {max} attempts");
    }

    // ---- Conditions --------------------------------------------------------------------------------

    public static bool Evaluate(FlowCondition condition, VariableContext variables)
    {
        var left = variables.Resolve(condition.Left);
        var right = variables.Resolve(condition.Right);
        return condition.Comparison switch
        {
            FlowComparison.Equals => NumberOrString(left, right, (a, b) => a == b, (a, b) => string.Equals(a, b, StringComparison.Ordinal)),
            FlowComparison.NotEquals => !NumberOrString(left, right, (a, b) => a == b, (a, b) => string.Equals(a, b, StringComparison.Ordinal)),
            FlowComparison.Contains => left.Contains(right, StringComparison.OrdinalIgnoreCase),
            FlowComparison.GreaterThan => Numeric(left, right, (a, b) => a > b),
            FlowComparison.LessThan => Numeric(left, right, (a, b) => a < b),
            FlowComparison.Matches => SafeMatch(left, right),
            FlowComparison.IsTruthy => IsTruthy(left),
            FlowComparison.IsEmpty => string.IsNullOrWhiteSpace(left) || left is "[]" or "{}" or "null",
            _ => false
        };
    }

    private static bool NumberOrString(string a, string b, Func<double, double, bool> numeric, Func<string, string, bool> text) =>
        TryNumber(a, out var x) && TryNumber(b, out var y) ? numeric(x, y) : text(a, b);

    private static bool Numeric(string a, string b, Func<double, double, bool> compare) =>
        TryNumber(a, out var x) && TryNumber(b, out var y) && compare(x, y);

    // Culture-invariant: "1.5" must mean one and a half regardless of the OS locale.
    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static bool IsTruthy(string value) =>
        value.Length > 0 && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value is not ("0" or "null" or "[]" or "{}");

    private static bool SafeMatch(string input, string pattern)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(input, pattern, System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Label(FlowStep step, State state)
    {
        if (step.Name.Length > 0)
            return step.Name;
        return step.Type switch
        {
            FlowStepType.Request => step.RequestId is { } id && state.Requests.TryGetValue(id, out var r) ? r.Name : "Request",
            FlowStepType.SetVariable => $"Set {step.Variable}",
            FlowStepType.Delay => "Delay",
            FlowStepType.If => $"If {step.Condition.Left} {step.Condition.Comparison}",
            FlowStepType.Repeat => $"Repeat {step.Count}×",
            FlowStepType.ForEach => "For each",
            FlowStepType.Until => "Until",
            FlowStepType.Stop => step.Fail ? "Fail" : "Stop",
            FlowStepType.Script => "Script",
            _ => step.Type.ToString()
        };
    }

    private static void Emit(IProgress<FlowEvent>? progress, FlowResult result, FlowEvent e)
    {
        result.Events.Add(e);
        if (!e.Ok)
            result.Passed = false;
        progress?.Report(e);
    }

    private static string Truncate(string s) => s.Length > 80 ? s[..77] + "…" : s;
}
