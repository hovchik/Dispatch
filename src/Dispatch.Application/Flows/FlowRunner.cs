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
}

public enum FlowEventKind
{
    StepStarted,
    StepFinished,
    Skipped,
    Info,
    Failed
}

/// <summary>A line in the running log of a flow.</summary>
public sealed record FlowEvent(FlowEventKind Kind, int Depth, string StepName, string Detail = "", bool Ok = true,
    ApiResponse? Response = null);

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

        foreach (var (k, v) in variables.EnvironmentUpdates)
            result.EnvironmentUpdates[k] = v;
        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private sealed record State(FlowRunOptions Options, VariableContext Variables, IReadOnlyDictionary<Guid, ApiRequest> Requests,
        FlowResult Result, IProgress<FlowEvent>? Progress);

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
                Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.Info, depth, name, $"{step.Milliseconds} ms"));
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
        var response = await sender.SendAsync(request, new SendOptions
        {
            Variables = state.Variables,
            CollectionSpec = state.Options.CollectionSpec,
            CollectionRequests = state.Options.Requests,
            RecordHistory = state.Options.RecordHistory
        }, ct).ConfigureAwait(false);

        state.Result.RequestsSent++;
        state.Result.TestsPassed += response.TestResults.Count(t => t.Passed);
        state.Result.TestsFailed += response.TestResults.Count(t => !t.Passed);
        var ok = response.HasResponse && response.AllTestsPassed;
        var detail = response.HasResponse
            ? $"{response.StatusCode} {response.ReasonPhrase} · {response.Elapsed.TotalMilliseconds:0} ms" +
              (response.TestResults.Count > 0 ? $" · {response.TestResults.Count(t => t.Passed)}/{response.TestResults.Count} tests" : "")
            : $"error: {response.Error}";
        Emit(state.Progress, state.Result, new FlowEvent(FlowEventKind.StepFinished, depth, name, detail, ok, response));

        if (!ok && !step.ContinueOnError)
            throw new StopFlow(true, $"{name}: {detail}");
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
            if (attempt < max && step.Milliseconds > 0)
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
        double.TryParse(a, out var x) && double.TryParse(b, out var y) ? numeric(x, y) : text(a, b);

    private static bool Numeric(string a, string b, Func<double, double, bool> compare) =>
        double.TryParse(a, out var x) && double.TryParse(b, out var y) && compare(x, y);

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
