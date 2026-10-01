using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.Application.Impact;

public enum ImpactSeverity
{
    /// <summary>Will fail: it reads a field that is gone or now has another type.</summary>
    Breaks,

    /// <summary>May fail: a heuristic match (script text, a parent object compared as a whole, a chained variable).</summary>
    Possible
}

public enum ImpactKind
{
    Assertion,
    Snapshot,
    Extraction,
    Script,
    VariableUse,
    Example,
    Flow
}

/// <summary>One thing in the workspace that depends on a changed part of a response.</summary>
public sealed record ImpactItem(
    ImpactSeverity Severity,
    ImpactKind Kind,
    string Owner,
    string Location,
    string Detail,
    string? Suggestion = null)
{
    public string SeverityText => Severity == ImpactSeverity.Breaks ? "BREAKS" : "POSSIBLE";
}

public sealed class ImpactReport
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required string RequestName { get; init; }
    public string BaselineName { get; init; } = "baseline";
    public IReadOnlyList<ShapeChange> Changes { get; init; } = [];
    public List<ImpactItem> Items { get; } = [];

    /// <summary>Variables that will no longer be set (or set to the wrong thing), with why.</summary>
    public Dictionary<string, string> BrokenVariables { get; } = new(StringComparer.Ordinal);

    public IEnumerable<ShapeChange> BreakingChanges => Changes.Where(c => c.Kind != ShapeChangeKind.Added);
    public bool HasBreakingImpact => Items.Any(i => i.Severity == ImpactSeverity.Breaks);

    public int Count(ImpactKind kind, ImpactSeverity? severity = null) =>
        Items.Count(i => i.Kind == kind && (severity is null || i.Severity == severity));

    /// <summary>One line, e.g. "Renamed $.user.id → $.user.userId breaks 3 tests, 1 extraction, 2 requests and 1 flow.".</summary>
    public string Summary
    {
        get
        {
            var breaking = BreakingChanges.ToList();
            if (breaking.Count == 0)
                return Changes.Count == 0 ? "No structural change." : $"Only additions ({Changes.Count}); nothing depends on removed fields.";
            var what = breaking.Count == 1 ? breaking[0].ToString() : $"{breaking.Count} structural changes";
            var parts = new List<string>();
            void Part(int n, string singular, string plural)
            {
                if (n > 0)
                    parts.Add($"{n} {(n == 1 ? singular : plural)}");
            }
            var breaks = Items.Where(i => i.Severity == ImpactSeverity.Breaks).ToList();
            Part(breaks.Count(i => i.Kind is ImpactKind.Assertion or ImpactKind.Snapshot), "test", "tests");
            Part(breaks.Count(i => i.Kind == ImpactKind.Extraction), "extraction", "extractions");
            Part(breaks.Where(i => i.Kind == ImpactKind.VariableUse).Select(i => i.Owner).Distinct().Count(), "request", "requests");
            Part(breaks.Count(i => i.Kind == ImpactKind.Flow), "flow", "flows");
            var possible = Items.Count(i => i.Severity == ImpactSeverity.Possible);
            var verb = breaking.Count == 1 ? "breaks" : "break";
            var verdict = parts.Count == 0 ? $"{verb} nothing directly" : $"{verb} " + JoinList(parts);
            return $"{char.ToUpperInvariant(what[0])}{what[1..]} {verdict}" + (possible > 0 ? $" ({possible} more to check)" : "") + ".";
        }
    }

    private static string JoinList(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}

/// <summary>
/// Change impact map: given how a response's shape changed (fields removed, renamed or retyped), finds everything in the
/// collection that depends on those fields — the request's own JSONPath assertions, snapshots, extraction rules, test
/// script and saved examples — then follows each variable that is no longer set correctly to the requests, scripts and
/// test flows that use it, transitively.
/// </summary>
public static class ImpactAnalyzer
{
    private const int MaxChainDepth = 4;

    public static ImpactReport Analyze(ApiRequest request, string before, string after, IReadOnlyList<ApiRequest> collectionRequests,
        IReadOnlyList<TestFlow>? flows = null, string baselineName = "baseline") =>
        Analyze(request, JsonShape.Diff(before, after), after, collectionRequests, flows, baselineName);

    public static ImpactReport Analyze(ApiRequest request, IReadOnlyList<ShapeChange> changes, string? after,
        IReadOnlyList<ApiRequest> collectionRequests, IReadOnlyList<TestFlow>? flows = null, string baselineName = "baseline")
    {
        var report = new ImpactReport { RequestName = request.Name, Changes = changes, BaselineName = baselineName };
        var breaking = changes.Where(c => c.Kind != ShapeChangeKind.Added).ToList();
        if (breaking.Count == 0)
            return report;

        // Requests that will fail or misbehave, for the flow pass at the end.
        var affectedRequests = new Dictionary<Guid, ImpactSeverity>();
        void MarkRequest(ApiRequest r, ImpactSeverity severity)
        {
            // Breaks (0) wins over Possible (1).
            if (!affectedRequests.TryGetValue(r.Id, out var known) || severity < known)
                affectedRequests[r.Id] = severity;
        }

        // ---- 1. Assertions and snapshots on the request itself ----
        foreach (var assertion in request.Assertions.Where(a => a.Enabled))
        {
            if (assertion.Source == ValueSource.Snapshot)
            {
                var ignore = Snapshots.ParseIgnorePaths(assertion.Path).Select(JsonShape.Split).ToList();
                var uncovered = breaking.Where(c => !ignore.Any(i => JsonShape.Relate(i, c.Segments) == JsonShape.Overlap.Inside)).ToList();
                if (uncovered.Count > 0 && assertion.Expected.Length > 0)
                {
                    report.Items.Add(new ImpactItem(ImpactSeverity.Breaks, ImpactKind.Snapshot, request.Name, "Tests · snapshot",
                        $"The stored snapshot has the old shape ({string.Join("; ", uncovered.Take(3))}{(uncovered.Count > 3 ? "; …" : "")}).",
                        "Re-record it (Reset in the Tests tab, or dispatch run --update-snapshots) once the change is intended."));
                    MarkRequest(request, ImpactSeverity.Breaks);
                }
                continue;
            }
            if (assertion.Source != ValueSource.JsonPath)
                continue;
            var path = JsonShape.Split(assertion.Path);
            foreach (var change in breaking)
            {
                if (Judge(path, change, assertion.Operator) is not { } verdict)
                    continue;
                report.Items.Add(new ImpactItem(verdict.Severity, ImpactKind.Assertion, request.Name, "Tests · " + AssertionEvaluator.Describe(assertion),
                    verdict.Detail, Suggest(assertion.Path, change)));
                MarkRequest(request, verdict.Severity);
                break;
            }
        }

        // ---- 2. Extraction rules: variables that will no longer be set ----
        var broken = new Queue<(string Variable, ImpactSeverity Severity, string Cause, int Depth)>();
        foreach (var rule in request.Extractions.Where(e => e.Enabled && e.Variable.Trim().Length > 0 && e.Source == ValueSource.JsonPath))
        {
            var path = JsonShape.Split(rule.Path);
            foreach (var change in breaking)
            {
                if (Judge(path, change, AssertionOperator.Equals) is not { } verdict)
                    continue;
                var variable = rule.Variable.Trim();
                var effect = change.Kind is ShapeChangeKind.Removed or ShapeChangeKind.Renamed
                    ? $"{{{{{variable}}}}} will no longer be set: {verdict.Detail}"
                    : $"{{{{{variable}}}}} may get a value of the wrong type: {verdict.Detail}";
                report.Items.Add(new ImpactItem(verdict.Severity, ImpactKind.Extraction, request.Name, $"Extract · {variable} ← {rule.Path}",
                    effect, Suggest(rule.Path, change)));
                broken.Enqueue((variable, verdict.Severity, $"extracted by {request.Name} from {rule.Path}", 0));
                break;
            }
        }

        // ---- 3. The request's own test script (heuristic: property chains of changed fields) ----
        foreach (var change in breaking.Where(c => c.Kind is ShapeChangeKind.Removed or ShapeChangeKind.Renamed))
            if (ScriptLine(request.TestScript, change) is { } line)
                report.Items.Add(new ImpactItem(ImpactSeverity.Possible, ImpactKind.Script, request.Name, $"Test script · line {line.Number}",
                    $"Looks like it reads {change.Path}: {line.Text}", change.NewPath is null ? null : $"Field is now {change.NewPath}."));

        // ---- 4. Saved examples (served by the mock server) that still have the old shape ----
        if (after is not null)
            foreach (var example in request.Examples)
            {
                var stale = JsonShape.Diff(example.Body, after).Where(d => d.Kind != ShapeChangeKind.Added
                    && breaking.Any(b => b.Path == d.Path)).ToList();
                if (stale.Count > 0)
                    report.Items.Add(new ImpactItem(ImpactSeverity.Possible, ImpactKind.Example, request.Name, $"Examples · {example.Name}",
                        $"Still has the old shape ({string.Join("; ", stale.Take(3))}), so mocks and docs show it.",
                        "Save the new response as an example, or update this one."));
            }

        // ---- 5. Follow broken variables through the collection ----
        var variableSeverity = new Dictionary<string, ImpactSeverity>(StringComparer.Ordinal);
        while (broken.Count > 0)
        {
            var (variable, severity, cause, depth) = broken.Dequeue();
            if (!variableSeverity.TryAdd(variable, severity))
                continue;
            report.BrokenVariables[variable] = cause;

            foreach (var other in collectionRequests.Where(r => r.Id != request.Id))
            {
                var uses = Usages(other, variable).ToList();
                if (uses.Count == 0)
                    continue;
                foreach (var location in uses)
                    report.Items.Add(new ImpactItem(severity, ImpactKind.VariableUse, other.Name, location,
                        $"Uses {{{{{variable}}}}}, which is {cause}" + (depth > 0 ? " (chained)" : "") + "."));
                MarkRequest(other, severity);

                // Whatever this request extracts may now be wrong too.
                if (depth + 1 < MaxChainDepth)
                    foreach (var rule in other.Extractions.Where(e => e.Enabled && e.Variable.Trim().Length > 0))
                        broken.Enqueue((rule.Variable.Trim(), ImpactSeverity.Possible, $"extracted by {other.Name}, which uses {{{{{variable}}}}}", depth + 1));
            }
        }

        // ---- 6. Test flows that run affected requests or use broken variables ----
        foreach (var flow in flows ?? [])
        {
            var reasons = new List<string>();
            var severity = ImpactSeverity.Possible;
            foreach (var (step, trail) in Steps(flow.Steps, ""))
            {
                if (!step.Enabled)
                    continue;
                if (step.Type == FlowStepType.Request && step.RequestId is { } id && affectedRequests.TryGetValue(id, out var s))
                {
                    var name = collectionRequests.FirstOrDefault(r => r.Id == id)?.Name ?? "request";
                    reasons.Add($"{trail}runs {name}");
                    if (s == ImpactSeverity.Breaks)
                        severity = ImpactSeverity.Breaks;
                }
                foreach (var variable in report.BrokenVariables.Keys)
                    if (StepUses(step, variable))
                    {
                        reasons.Add($"{trail}{StepLabel(step)} uses {{{{{variable}}}}}");
                        if (variableSeverity[variable] == ImpactSeverity.Breaks)
                            severity = ImpactSeverity.Breaks;
                    }
            }
            if (reasons.Count > 0)
                report.Items.Add(new ImpactItem(severity, ImpactKind.Flow, flow.Name, "Flow",
                    string.Join("; ", reasons.Distinct().Take(6)) + (reasons.Count > 6 ? "; …" : "")));
        }

        return report;
    }

    /// <summary>How reading <paramref name="path"/> with <paramref name="op"/> is affected by <paramref name="change"/>, or null.</summary>
    private static (ImpactSeverity Severity, string Detail)? Judge(IReadOnlyList<string> path, ShapeChange change, AssertionOperator op)
    {
        switch (JsonShape.Relate(path, change.Segments))
        {
            case JsonShape.Overlap.Inside:
                return change.Kind switch
                {
                    // Expecting the field to be absent still holds when it is gone.
                    ShapeChangeKind.Removed or ShapeChangeKind.Renamed when op is AssertionOperator.NotExists => null,
                    ShapeChangeKind.Removed => (ImpactSeverity.Breaks, $"reads {change.Path}, which was removed"),
                    ShapeChangeKind.Renamed => (ImpactSeverity.Breaks, $"reads {change.Path}, which is now {change.NewPath}"),
                    ShapeChangeKind.TypeChanged when op is AssertionOperator.Exists or AssertionOperator.NotExists => null,
                    ShapeChangeKind.TypeChanged when op is AssertionOperator.IsType or AssertionOperator.GreaterThan or AssertionOperator.GreaterOrEqual
                        or AssertionOperator.LessThan or AssertionOperator.LessOrEqual or AssertionOperator.LengthEquals =>
                        (ImpactSeverity.Breaks, $"{change.Path} is now {change.NewType} (was {change.OldType})"),
                    ShapeChangeKind.TypeChanged => (ImpactSeverity.Possible, $"{change.Path} is now {change.NewType} (was {change.OldType})"),
                    _ => null
                };
            case JsonShape.Overlap.Ancestor when op is AssertionOperator.Equals or AssertionOperator.NotEquals or AssertionOperator.Contains
                or AssertionOperator.NotContains or AssertionOperator.LengthEquals or AssertionOperator.Matches:
                return (ImpactSeverity.Possible, $"compares a value that contains {change.Path} ({change.Kind.ToString().ToLowerInvariant()})");
            default:
                return null;
        }
    }

    /// <summary>The reference path rewritten for a rename, when the change is a rename.</summary>
    private static string? Suggest(string referencePath, ShapeChange change)
    {
        if (change.Kind != ShapeChangeKind.Renamed || change.NewPath is null)
            return null;
        var reference = JsonShape.Split(referencePath);
        if (reference.Contains(JsonShape.Deep) || reference.Count < change.Segments.Count)
            return $"Point it at {change.NewPath}.";
        // Keep the reference's own index choices ([0] vs [*]) where the old path had a wildcard.
        var rest = reference.Skip(change.Segments.Count);
        var rewritten = JsonShape.Join(JsonShape.Split(change.NewPath).Concat(rest).ToList());
        return $"Change the path to {rewritten}.";
    }

    // ---- Variable usages -----------------------------------------------------------------------

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Where a request uses a variable: <c>{{name}}</c> anywhere, or pm.*.get("name") in scripts.</summary>
    internal static IEnumerable<string> Usages(ApiRequest r, string variable)
    {
        var placeholder = new Regex(@"\{\{\s*" + Regex.Escape(variable) + @"\s*\}\}");
        var getter = new Regex(@"\.get\(\s*['""]" + Regex.Escape(variable) + @"['""]\s*\)");
        bool Uses(string? text) => text is not null && placeholder.IsMatch(text);

        if (Uses(r.Url))
            yield return "URL";
        foreach (var p in r.QueryParams.Where(p => p.Enabled && (Uses(p.Key) || Uses(p.Value))))
            yield return $"Params · {p.Key}";
        foreach (var h in r.Headers.Where(h => h.Enabled && (Uses(h.Key) || Uses(h.Value))))
            yield return $"Headers · {h.Key}";
        if (r.Body.Mode != BodyMode.None && (Uses(r.Body.Content) || r.Body.FormFields.Any(f => f.Enabled && Uses(f.Value))))
            yield return "Body";
        if (r.Auth.Mode != AuthMode.None && Uses(JsonSerializer.Serialize(r.Auth, Compact)))
            yield return $"Auth ({r.Auth.Mode})";
        if (r.Kind != RequestKind.Http && Uses(JsonSerializer.Serialize(r.Protocol, Compact)))
            yield return $"{r.Kind} settings";
        foreach (var a in r.Assertions.Where(a => a.Enabled && (Uses(a.Expected) || Uses(a.Path))))
            yield return "Tests · " + AssertionEvaluator.Describe(a);
        if (r.Expectations.Any(e => e.Enabled && (Uses(e.Expected) || Uses(e.Path))))
            yield return "Messages";
        if (Uses(r.PreRequestScript) || getter.IsMatch(r.PreRequestScript))
            yield return "Pre-request script";
        if (Uses(r.TestScript) || getter.IsMatch(r.TestScript))
            yield return "Test script";
    }

    private static bool StepUses(FlowStep step, string variable)
    {
        var placeholder = new Regex(@"\{\{\s*" + Regex.Escape(variable) + @"\s*\}\}");
        var getter = new Regex(@"\.get\(\s*['""]" + Regex.Escape(variable) + @"['""]\s*\)");
        bool Uses(string text) => placeholder.IsMatch(text);
        return step.Type switch
        {
            FlowStepType.SetVariable or FlowStepType.ForEach => Uses(step.Value),
            FlowStepType.Script => Uses(step.Value) || getter.IsMatch(step.Value),
            FlowStepType.If or FlowStepType.Until => Uses(step.Condition.Left) || Uses(step.Condition.Right),
            FlowStepType.Stop => Uses(step.Value) || Uses(step.Condition.Left) || Uses(step.Condition.Right),
            _ => false
        };
    }

    private static string StepLabel(FlowStep step) => step.Name.Length > 0 ? $"step \"{step.Name}\"" : $"{step.Type} step";

    private static IEnumerable<(FlowStep Step, string Trail)> Steps(IEnumerable<FlowStep> steps, string trail)
    {
        foreach (var step in steps)
        {
            yield return (step, trail);
            foreach (var child in Steps(step.Children, step.Type == FlowStepType.Group && step.Name.Length > 0 ? $"{trail}{step.Name} › " : trail))
                yield return child;
        }
    }

    // ---- Scripts -------------------------------------------------------------------------------

    /// <summary>The first script line that seems to access the changed field (e.g. <c>json.user.id</c>, <c>body["user"]["id"]</c>).</summary>
    private static (int Number, string Text)? ScriptLine(string script, ShapeChange change)
    {
        if (string.IsNullOrWhiteSpace(script))
            return null;
        var keys = change.Segments.Where(s => s != JsonShape.Any && s != JsonShape.Deep).ToList();
        if (keys.Count == 0)
            return null;
        static string Access(string key) => $@"(?:\.\s*{Regex.Escape(key)}\b|\[\s*['""]{Regex.Escape(key)}['""]\s*\])";
        // The last two keys in order (with optional [index] between), or a lone top-level key.
        var pattern = keys.Count >= 2
            ? Access(keys[^2]) + @"(?:\s*\[[^\]]*\])*\s*" + Access(keys[^1])
            : Access(keys[0]);
        var regex = new Regex(pattern);
        var lines = script.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (regex.IsMatch(lines[i]))
                return (i + 1, lines[i].Trim().Length > 120 ? lines[i].Trim()[..117] + "…" : lines[i].Trim());
        return null;
    }
}

/// <summary>A previous version of a response to compare the current one with.</summary>
public sealed record ImpactBaseline(string Name, string Body);

public static class ImpactBaselines
{
    /// <summary>The baselines a saved request carries: its recorded snapshots, then its saved examples.</summary>
    public static IReadOnlyList<ImpactBaseline> For(ApiRequest request)
    {
        var list = new List<ImpactBaseline>();
        foreach (var snapshot in request.Assertions.Where(a => a.Source == ValueSource.Snapshot && a.Expected.Length > 0))
            list.Add(new ImpactBaseline(list.Any(b => b.Name == "snapshot") ? $"snapshot {list.Count + 1}" : "snapshot", snapshot.Expected));
        foreach (var example in request.Examples.Where(e => e.Body.Trim().Length > 0))
            list.Add(new ImpactBaseline($"example: {example.Name}", example.Body));
        return list;
    }
}
