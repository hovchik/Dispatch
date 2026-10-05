using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Impact;
using Dispatch.Domain;

namespace Dispatch.Application.Laws;

public enum LawKind
{
    /// <summary>Fields always present with a stable type.</summary>
    Shape,
    Enum,
    NonNegative,
    Format,
    DateOrder,
    CountEquals,
    TotalAtLeast,
    SumEquals,
    PageSize,
    Echo,
    RoundTrip,
    DeleteGone,
    StableGet
}

public enum LawConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// A property that held across the observed traffic of an endpoint ("API law"), with how often it was seen. When it
/// held in all but a few cases, it is an <see cref="IsAnomaly">anomaly</see>: the counterexamples are probably bugs.
/// </summary>
public sealed class ApiLaw
{
    public required string Endpoint { get; init; }
    public required LawKind Kind { get; init; }
    public required string Description { get; init; }

    /// <summary>Observations the law was checked against.</summary>
    public int Support { get; init; }

    /// <summary>Cases that broke the law (empty for a law that always held).</summary>
    public List<string> Counterexamples { get; init; } = [];
    public bool IsAnomaly => Counterexamples.Count > 0;

    public LawConfidence Confidence => Support >= 20 ? LawConfidence.High : Support >= 5 ? LawConfidence.Medium : LawConfidence.Low;

    /// <summary>A no-code assertion that checks the law on every response (when one can express it).</summary>
    public Assertion? Assertion { get; init; }

    /// <summary>A pm.test snippet that checks the law on every response (when an assertion can't).</summary>
    public string? Script { get; init; }

    /// <summary>For stateful laws: how to test it (e.g. as a flow).</summary>
    public string? Advice { get; init; }

    public bool CanBecomeTest => Assertion is not null || Script is not null;
}

public sealed class LawOptions
{
    /// <summary>Successful responses an endpoint needs before laws about it are inferred.</summary>
    public int MinSamples { get; init; } = 3;

    /// <summary>An almost-law needs at least this many observations before its few counterexamples are called anomalies.</summary>
    public int MinSamplesForAnomaly { get; init; } = 10;

    /// <summary>Largest share of counterexamples an almost-law may have (at least one is always allowed).</summary>
    public double AnomalyRate { get; init; } = 0.05;
}

public sealed class LawReport
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string Source { get; init; } = string.Empty;
    public int Observations { get; init; }
    public IReadOnlyList<(string Endpoint, int Samples)> Endpoints { get; init; } = [];
    public List<ApiLaw> Laws { get; } = [];
    public IEnumerable<ApiLaw> Anomalies => Laws.Where(l => l.IsAnomaly);

    public string Summary => Laws.Count == 0
        ? $"No laws inferred from {Observations} observation(s). Each endpoint needs a few successful JSON responses."
        : $"{Laws.Count(l => !l.IsAnomaly)} law(s) and {Laws.Count(l => l.IsAnomaly)} anomal{(Laws.Count(l => l.IsAnomaly) == 1 ? "y" : "ies")} " +
          $"across {Laws.Select(l => l.Endpoint).Distinct().Count()} endpoint(s), from {Observations} observation(s).";
}

/// <summary>
/// Infers "API laws" from observed traffic — not a schema, but the semantic rules responses keep: required fields and
/// types, enumerations, non-negative numbers, formats, date ordering, counts and totals that match their arrays, page
/// sizes that respect the limit, request fields echoed back, created resources that can be read back, deleted ones that
/// return 404, and GETs that are repeatable (apart from volatile fields). Rules that held in all but a few cases are
/// reported as anomalies, with the cases that broke them.
/// </summary>
public static partial class LawMiner
{
    public static LawReport Mine(IReadOnlyList<Observation> observations, LawOptions? options = null, string source = "")
    {
        options ??= new LawOptions();
        var ordered = Observations.LearnTemplates(observations.OrderBy(o => o.At).ToList()).ToList();
        var byEndpoint = ordered.GroupBy(o => o.Endpoint).ToList();
        var report = new LawReport
        {
            Source = source,
            Observations = ordered.Count,
            Endpoints = byEndpoint.Select(g => (g.Key, g.Count(o => o.Succeeded))).OrderBy(e => e.Key, StringComparer.Ordinal).ToList()
        };

        foreach (var group in byEndpoint)
        {
            var samples = group.Where(o => o.Succeeded).Select(o => (Observation: o, Json: Parse(o.ResponseBody)))
                .Where(s => s.Json is not null).Select(s => (s.Observation, Json: s.Json!)).ToList();
            if (samples.Count >= options.MinSamples)
                report.Laws.AddRange(ResponseLaws(group.Key, samples, options));
            report.Laws.AddRange(PageSizeLaws(group.Key, samples, options));
            report.Laws.AddRange(EchoLaws(group.Key, samples, options));
        }
        report.Laws.AddRange(RoundTripLaws(ordered));
        report.Laws.AddRange(DeleteLaws(ordered));
        report.Laws.AddRange(StableGetLaws(ordered, options));

        // Anomalies first, then by endpoint.
        var sorted = report.Laws.OrderByDescending(l => l.IsAnomaly).ThenBy(l => l.Endpoint, StringComparer.Ordinal).ThenBy(l => l.Kind).ToList();
        report.Laws.Clear();
        report.Laws.AddRange(sorted);
        return report;
    }

    // ---- Single-response laws ------------------------------------------------------------------

    /// <summary>Every object occurrence at one container path (e.g. <c>$.items[*]</c>), with its sample index.</summary>
    private sealed record Container(IReadOnlyList<string> Path, List<(int Sample, JsonObject Node)> Occurrences);

    private static IEnumerable<ApiLaw> ResponseLaws(string endpoint, List<(Observation Observation, JsonNode Json)> samples, LawOptions options)
    {
        var containers = new Dictionary<string, Container>(StringComparer.Ordinal);
        for (var i = 0; i < samples.Count; i++)
            Collect(samples[i].Json, [], i, containers);

        foreach (var container in containers.Values.Where(c => c.Occurrences.Count >= options.MinSamples))
        {
            var where = JsonShape.Join(container.Path);
            var occurrences = container.Occurrences;
            var keys = occurrences.SelectMany(o => o.Node.Select(kv => kv.Key)).Distinct(StringComparer.Ordinal).ToList();

            // Shape: fields that are always present with one type.
            var shape = new List<(string Key, string Type)>();
            foreach (var key in keys)
            {
                var present = occurrences.Where(o => o.Node.ContainsKey(key)).ToList();
                var types = present.Select(o => TypeOf(o.Node[key])).Where(t => t != "null").Distinct().ToList();
                var missing = occurrences.Count - present.Count;
                if (missing == 0 && types.Count == 1)
                    shape.Add((key, types[0]));
                else if (missing > 0 && IsFewExceptions(missing, occurrences.Count, options))
                    yield return Anomaly(endpoint, LawKind.Shape, $"{Field(container.Path, key)} is present in every response except {missing} of {occurrences.Count}",
                        occurrences.Count, occurrences.Where(o => !o.Node.ContainsKey(key)).Select(o => Where(samples, o.Sample)));
                else if (missing == 0 && types.Count == 2 && options.MinSamplesForAnomaly <= present.Count)
                {
                    var odd = types.OrderBy(t => present.Count(o => TypeOf(o.Node[key]) == t)).First();
                    var oddCount = present.Count(o => TypeOf(o.Node[key]) == odd);
                    if (IsFewExceptions(oddCount, present.Count, options))
                        yield return Anomaly(endpoint, LawKind.Shape,
                            $"{Field(container.Path, key)} is a {types.First(t => t != odd)} except {oddCount} time(s) a {odd}", present.Count,
                            present.Where(o => TypeOf(o.Node[key]) == odd).Select(o => $"{Where(samples, o.Sample)}: {Short(o.Node[key])}"));
                }
            }
            if (shape.Count > 0)
                yield return new ApiLaw
                {
                    Endpoint = endpoint, Kind = LawKind.Shape, Support = occurrences.Count,
                    Description = $"{where} always has " + Fields(shape.Select(s => $"{s.Key} ({s.Type})")),
                    Script = Scripts.Shape(container.Path, shape)
                };

            // Per-field value laws.
            foreach (var key in keys)
            {
                var values = occurrences.Where(o => o.Node[key] is JsonValue).Select(o => (o.Sample, Value: (JsonValue)o.Node[key]!)).ToList();
                if (values.Count < options.MinSamples)
                    continue;
                var path = container.Path.Append(key).ToList();
                foreach (var law in ValueLaws(endpoint, path, values, samples, options))
                    yield return law;
            }

            // Relations between fields of the same object.
            foreach (var law in RelationLaws(endpoint, container, samples, options))
                yield return law;
        }
    }

    private static void Collect(JsonNode? node, List<string> path, int sample, Dictionary<string, Container> containers)
    {
        switch (node)
        {
            case JsonObject obj:
                var key = JsonShape.Join(path);
                if (!containers.TryGetValue(key, out var container))
                    containers[key] = container = new Container(path.ToList(), []);
                container.Occurrences.Add((sample, obj));
                foreach (var (name, child) in obj)
                    Collect(child, [.. path, name], sample, containers);
                break;
            case JsonArray array:
                foreach (var item in array)
                    Collect(item, [.. path, JsonShape.Any], sample, containers);
                break;
        }
    }

    private static IEnumerable<ApiLaw> ValueLaws(string endpoint, List<string> path, List<(int Sample, JsonValue Value)> values,
        List<(Observation Observation, JsonNode Json)> samples, LawOptions options)
    {
        var field = JsonShape.Join(path);
        var strings = values.Where(v => v.Value.GetValueKind() == JsonValueKind.String).Select(v => (v.Sample, Text: v.Value.GetValue<string>())).ToList();
        var numbers = values.Where(v => v.Value.GetValueKind() == JsonValueKind.Number).Select(v => (v.Sample, Number: v.Value.GetValue<double>())).ToList();

        if (strings.Count >= options.MinSamples && strings.Count == values.Count)
        {
            // Format (uuid, email, date-time, ...).
            var format = Formats.FirstOrDefault(f => strings.Count(s => f.Regex.IsMatch(s.Text)) >= strings.Count - Allowed(strings.Count, options)
                                                     && strings.Any(s => f.Regex.IsMatch(s.Text)));
            if (format is not null)
            {
                var bad = strings.Where(s => !format.Regex.IsMatch(s.Text)).ToList();
                yield return bad.Count == 0
                    ? new ApiLaw
                    {
                        Endpoint = endpoint, Kind = LawKind.Format, Support = strings.Count, Description = $"{field} is always a {format.Name}",
                        Assertion = TopLevel(path) ? new Assertion { Source = ValueSource.JsonPath, Path = field, Operator = AssertionOperator.Matches, Expected = format.Pattern } : null,
                        Script = TopLevel(path) ? null : Scripts.Each(path, $"is a {format.Name}", $"/{format.Pattern}/.test(v)")
                    }
                    : Anomaly(endpoint, LawKind.Format, $"{field} is a {format.Name} except {bad.Count} of {strings.Count}", strings.Count,
                        bad.Select(b => $"{Where(samples, b.Sample)}: \"{Short(b.Text)}\""));
            }
            else if (!LooksLikeIdentifier(path[^1]))
            {
                // Enumeration: few distinct short values, each seen more than once.
                var counts = strings.GroupBy(s => s.Text).Select(g => (Value: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
                var common = counts.Where(c => c.Count >= 2).ToList();
                var rare = counts.Where(c => c.Count == 1).ToList();
                if (common.Count is >= 1 and <= 6 && counts.All(c => c.Value.Length <= 40) && strings.Count >= 2 * common.Count + rare.Count)
                {
                    // A rare value that is a near-miss of a common one ("actve" vs "active") is a likely bug.
                    var typos = rare.Where(r => common.Any(c => Distance(r.Value, c.Value) <= 2 && r.Value.Length >= 3)).ToList();
                    if (rare.Count == 0)
                    {
                        var allowed = common.Select(c => c.Value).Order(StringComparer.Ordinal).ToList();
                        yield return new ApiLaw
                        {
                            Endpoint = endpoint, Kind = LawKind.Enum, Support = strings.Count,
                            Description = allowed.Count == 1 ? $"{field} is always \"{allowed[0]}\"" : $"{field} is one of " + string.Join(", ", allowed.Select(a => $"\"{a}\"")),
                            Assertion = TopLevel(path)
                                ? new Assertion { Source = ValueSource.JsonPath, Path = field, Operator = AssertionOperator.OneOf, Expected = string.Join(" | ", allowed) }
                                : null,
                            Script = TopLevel(path) ? null : Scripts.Each(path, "is one of " + string.Join(", ", allowed), $"{JsonSerializer.Serialize(allowed)}.includes(v)")
                        };
                    }
                    else if (typos.Count > 0 && strings.Count >= options.MinSamplesForAnomaly)
                        yield return Anomaly(endpoint, LawKind.Enum,
                            $"{field} is one of {string.Join(", ", common.Select(c => $"\"{c.Value}\""))}, but once {string.Join(", ", typos.Select(t => $"\"{t.Value}\""))} (a likely typo)",
                            strings.Count, typos.Select(t => $"{Where(samples, strings.First(s => s.Text == t.Value).Sample)}: \"{t.Value}\""));
                }
            }
        }

        if (numbers.Count >= options.MinSamples && numbers.Count == values.Count && numbers.Any(n => n.Number > 0))
        {
            var negative = numbers.Where(n => n.Number < 0).ToList();
            if (negative.Count == 0)
                yield return new ApiLaw
                {
                    Endpoint = endpoint, Kind = LawKind.NonNegative, Support = numbers.Count, Description = $"{field} is never negative",
                    Assertion = TopLevel(path) ? new Assertion { Source = ValueSource.JsonPath, Path = field, Operator = AssertionOperator.GreaterOrEqual, Expected = "0" } : null,
                    Script = TopLevel(path) ? null : Scripts.Each(path, "is not negative", "v >= 0")
                };
            else if (IsFewExceptions(negative.Count, numbers.Count, options))
                yield return Anomaly(endpoint, LawKind.NonNegative, $"{field} is never negative except {negative.Count} of {numbers.Count}", numbers.Count,
                    negative.Select(n => $"{Where(samples, n.Sample)}: {Num(n.Number)}"));
        }
    }

    private static IEnumerable<ApiLaw> RelationLaws(string endpoint, Container container, List<(Observation Observation, JsonNode Json)> samples, LawOptions options)
    {
        var occurrences = container.Occurrences;
        var keys = occurrences.SelectMany(o => o.Node.Select(kv => kv.Key)).Distinct(StringComparer.Ordinal).ToList();
        string F(string key) => Field(container.Path, key);

        // Dates that are always in order (createdAt <= updatedAt).
        var dateKeys = keys.Where(k => occurrences.Count(o => o.Node[k] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                                                            && IsDate(v.GetValue<string>())) >= options.MinSamples).ToList();
        foreach (var a in dateKeys)
            foreach (var b in dateKeys.Where(b => string.CompareOrdinal(a, b) != 0))
            {
                var pairs = occurrences.Where(o => Date(o.Node[a]) is not null && Date(o.Node[b]) is not null)
                    .Select(o => (o.Sample, A: Date(o.Node[a])!.Value, B: Date(o.Node[b])!.Value)).ToList();
                if (pairs.Count < options.MinSamples || !pairs.Any(p => p.A < p.B))
                    continue;
                var broken = pairs.Where(p => p.A > p.B).ToList();
                if (broken.Count == 0)
                    yield return new ApiLaw
                    {
                        Endpoint = endpoint, Kind = LawKind.DateOrder, Support = pairs.Count, Description = $"{F(a)} is never after {F(b)}",
                        Script = Scripts.PerObject(container.Path, $"{a} <= {b}",
                            $"o[{Js(a)}] == null || o[{Js(b)}] == null || Date.parse(o[{Js(a)}]) <= Date.parse(o[{Js(b)}])")
                    };
                else if (IsFewExceptions(broken.Count, pairs.Count, options) && broken.Count < pairs.Count(p => p.A < p.B))
                    yield return Anomaly(endpoint, LawKind.DateOrder, $"{F(a)} is never after {F(b)}, except {broken.Count} of {pairs.Count}", pairs.Count,
                        broken.Select(p => $"{Where(samples, p.Sample)}: {a} {p.A:O} > {b} {p.B:O}"));
            }

        // Counts and totals that match an array of the same object.
        var arrays = keys.Where(k => occurrences.Count(o => o.Node[k] is JsonArray) >= options.MinSamples).ToList();
        var numberKeys = keys.Where(k => occurrences.Count(o => o.Node[k] is JsonValue v && v.GetValueKind() == JsonValueKind.Number) >= options.MinSamples).ToList();
        foreach (var arrayKey in arrays)
            foreach (var numberKey in numberKeys)
            {
                var pairs = occurrences.Where(o => o.Node[arrayKey] is JsonArray && o.Node[numberKey] is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                    .Select(o => (o.Sample, Length: ((JsonArray)o.Node[arrayKey]!).Count, Value: o.Node[numberKey]!.GetValue<double>())).ToList();
                if (pairs.Count < options.MinSamples)
                    continue;
                var named = CountName().IsMatch(numberKey);
                var lengthsVary = pairs.Select(p => p.Length).Distinct().Count() > 1;
                if (pairs.All(p => p.Value == p.Length) && (named || lengthsVary))
                    yield return new ApiLaw
                    {
                        Endpoint = endpoint, Kind = LawKind.CountEquals, Support = pairs.Count,
                        Description = $"{F(numberKey)} always equals the number of {F(arrayKey)}",
                        Script = Scripts.PerObject(container.Path, $"{numberKey} == {arrayKey}.length",
                            $"!Array.isArray(o[{Js(arrayKey)}]) || o[{Js(numberKey)}] === o[{Js(arrayKey)}].length")
                    };
                else if (TotalName().IsMatch(numberKey) && pairs.All(p => p.Value >= p.Length) && pairs.Any(p => p.Value > p.Length))
                    yield return new ApiLaw
                    {
                        Endpoint = endpoint, Kind = LawKind.TotalAtLeast, Support = pairs.Count,
                        Description = $"{F(numberKey)} is never less than the number of {F(arrayKey)} returned",
                        Script = Scripts.PerObject(container.Path, $"{numberKey} >= {arrayKey}.length",
                            $"!Array.isArray(o[{Js(arrayKey)}]) || o[{Js(numberKey)}] >= o[{Js(arrayKey)}].length")
                    };
                else if (named && pairs.Count >= options.MinSamplesForAnomaly)
                {
                    var off = pairs.Where(p => p.Value != p.Length).ToList();
                    if (IsFewExceptions(off.Count, pairs.Count, options))
                        yield return Anomaly(endpoint, LawKind.CountEquals, $"{F(numberKey)} equals the number of {F(arrayKey)} except {off.Count} of {pairs.Count}",
                            pairs.Count, off.Select(p => $"{Where(samples, p.Sample)}: {numberKey} {Num(p.Value)} but {p.Length} item(s)"));
                }
            }

        // Totals that are the sum of a field of the items (total == sum(items[*].price)).
        foreach (var arrayKey in arrays)
        {
            var itemKeys = occurrences.Where(o => o.Node[arrayKey] is JsonArray).SelectMany(o => ((JsonArray)o.Node[arrayKey]!).OfType<JsonObject>())
                .SelectMany(i => i.Where(kv => kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.Number).Select(kv => kv.Key)).Distinct().ToList();
            foreach (var itemKey in itemKeys)
                foreach (var numberKey in numberKeys)
                {
                    var pairs = occurrences.Where(o => o.Node[arrayKey] is JsonArray && o.Node[numberKey] is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                        .Select(o => (o.Sample, Items: ((JsonArray)o.Node[arrayKey]!).OfType<JsonObject>().ToList(), Value: o.Node[numberKey]!.GetValue<double>()))
                        .Where(p => p.Items.Count > 0).ToList();
                    if (pairs.Count < options.MinSamples || pairs.All(p => p.Value == 0))
                        continue;
                    static double Sum(List<JsonObject> items, string key) =>
                        items.Sum(i => i[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : 0);
                    var off = pairs.Where(p => Math.Abs(Sum(p.Items, itemKey) - p.Value) > 0.005 * Math.Max(1, Math.Abs(p.Value))).ToList();
                    // Sums of one item are just copies; require at least one response with two or more items.
                    if (!pairs.Any(p => p.Items.Count > 1))
                        continue;
                    if (off.Count == 0)
                        yield return new ApiLaw
                        {
                            Endpoint = endpoint, Kind = LawKind.SumEquals, Support = pairs.Count,
                            Description = $"{F(numberKey)} always equals the sum of {F(arrayKey)}[*].{itemKey}",
                            Script = Scripts.PerObject(container.Path, $"{numberKey} == sum of {arrayKey}[*].{itemKey}",
                                $"!Array.isArray(o[{Js(arrayKey)}]) || Math.abs(o[{Js(arrayKey)}].reduce((s, i) => s + (Number(i[{Js(itemKey)}]) || 0), 0) - o[{Js(numberKey)}]) <= 0.005 * Math.max(1, Math.abs(o[{Js(numberKey)}]))")
                        };
                    else if (IsFewExceptions(off.Count, pairs.Count, options) && pairs.Count >= options.MinSamplesForAnomaly)
                        yield return Anomaly(endpoint, LawKind.SumEquals, $"{F(numberKey)} equals the sum of {F(arrayKey)}[*].{itemKey} except {off.Count} of {pairs.Count}",
                            pairs.Count, off.Select(p => $"{Where(samples, p.Sample)}: {numberKey} {Num(p.Value)}, sum {Num(Sum(p.Items, itemKey))}"));
                }
        }
    }

    // ---- Request ↔ response laws --------------------------------------------------------------

    private static readonly string[] PageParams = ["limit", "size", "per_page", "perPage", "pageSize", "page_size", "take", "top", "max", "count"];

    /// <summary>Arrays never longer than the limit/size query parameter.</summary>
    private static IEnumerable<ApiLaw> PageSizeLaws(string endpoint, List<(Observation Observation, JsonNode Json)> samples, LawOptions options)
    {
        foreach (var param in PageParams)
        {
            var withParam = samples.Select(s => (s.Observation, s.Json, Limit: QueryNumber(s.Observation.Url, param))).Where(s => s.Limit is > 0).ToList();
            if (withParam.Count < options.MinSamples)
                continue;
            var arrayPaths = withParam.SelectMany(s => TopArrays(s.Json)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var arrayPath in arrayPaths)
            {
                var pairs = withParam.Select(s => (s.Observation, s.Limit, Length: ArrayAt(s.Json, arrayPath)?.Count)).Where(p => p.Length is not null).ToList();
                if (pairs.Count < options.MinSamples || !pairs.Any(p => p.Length == (int)p.Limit!.Value))
                    continue;
                var over = pairs.Where(p => p.Length > p.Limit).ToList();
                if (over.Count == 0)
                    yield return new ApiLaw
                    {
                        Endpoint = endpoint, Kind = LawKind.PageSize, Support = pairs.Count,
                        Description = $"{arrayPath} never has more items than the {param} query parameter",
                        Script = Scripts.PageSize(arrayPath, param)
                    };
                else if (IsFewExceptions(over.Count, pairs.Count, options) && pairs.Count >= options.MinSamplesForAnomaly)
                    yield return Anomaly(endpoint, LawKind.PageSize, $"{arrayPath} respects ?{param}= except {over.Count} of {pairs.Count}", pairs.Count,
                        over.Select(p => $"{p.Observation.At:HH:mm:ss} {p.Observation.Url}: {p.Length} item(s) for {param}={Num(p.Limit!.Value)}"));
            }
        }
    }

    /// <summary>POST / PUT / PATCH responses that return the submitted fields unchanged.</summary>
    private static IEnumerable<ApiLaw> EchoLaws(string endpoint, List<(Observation Observation, JsonNode Json)> samples, LawOptions options)
    {
        if (!(endpoint.StartsWith("POST ", StringComparison.Ordinal) || endpoint.StartsWith("PUT ", StringComparison.Ordinal)
                                                                    || endpoint.StartsWith("PATCH ", StringComparison.Ordinal)))
            yield break;
        var pairs = samples.Select(s => (s.Observation, Request: Parse(s.Observation.RequestBody) as JsonObject, Response: s.Json))
            .Where(p => p.Request is not null).ToList();
        var minimum = Math.Max(2, options.MinSamples - 1);
        if (pairs.Count < minimum)
            yield break;

        var echoed = new List<string>();
        var anomalies = new List<ApiLaw>();
        var fields = pairs.SelectMany(p => p.Request!.Where(kv => kv.Value is JsonValue).Select(kv => kv.Key)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var field in fields)
        {
            var checks = pairs.Where(p => p.Request![field] is JsonValue)
                .Select(p => (p.Observation, Sent: p.Request![field]!.ToJsonString(), Got: EchoTarget(p.Response, field)?.ToJsonString())).ToList();
            if (checks.Count < minimum || checks.Any(c => c.Got is null))
                continue;
            var changed = checks.Where(c => c.Sent != c.Got).ToList();
            if (changed.Count == 0)
                echoed.Add(field);
            else if (IsFewExceptions(changed.Count, checks.Count, options) && checks.Count >= options.MinSamplesForAnomaly)
                anomalies.Add(Anomaly(endpoint, LawKind.Echo, $"The response returns the submitted {field} unchanged, except {changed.Count} of {checks.Count}", checks.Count,
                    changed.Select(c => $"{c.Observation.At:HH:mm:ss}: sent {Short(c.Sent)}, got {Short(c.Got!)}")));
        }
        if (echoed.Count > 0)
            yield return new ApiLaw
            {
                Endpoint = endpoint, Kind = LawKind.Echo, Support = pairs.Count,
                Description = "The response returns the submitted " + Fields(echoed) + " unchanged",
                Script = Scripts.Echo(echoed)
            };
        foreach (var anomaly in anomalies)
            yield return anomaly;
    }

    // ---- Stateful laws (sequences of calls) -----------------------------------------------------

    /// <summary>A created resource (POST response with an id) is later readable at a GET whose path contains that id.</summary>
    private static IEnumerable<ApiLaw> RoundTripLaws(List<Observation> ordered)
    {
        var results = new Dictionary<(string Create, string Read), (int Support, List<string> Counter)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var create = ordered[i];
            if (create.Method != "POST" || !create.Succeeded || Parse(create.ResponseBody) is not JsonObject created || IdOf(created) is not { } id)
                continue;
            var read = ordered.Skip(i + 1).FirstOrDefault(o => o.Method == "GET" && o.Path.Split('/').Contains(id)
                                                                && !ordered.Skip(i + 1).TakeWhile(x => x != o).Any(x => x.Method is "DELETE" && x.Path.Split('/').Contains(id)));
            if (read is null)
                continue;
            var key = (create.Endpoint, read.Endpoint);
            if (!results.TryGetValue(key, out var r))
                results[key] = r = (0, []);
            string? problem = null;
            if (!read.Succeeded)
                problem = $"{read.At:HH:mm:ss} {read.Method} {read.Path} returned {read.Status} right after it was created";
            else if (Parse(create.RequestBody) is JsonObject sent && Parse(read.ResponseBody) is { } stored)
            {
                var differences = sent.Where(kv => kv.Value is JsonValue && EchoTarget(stored, kv.Key) is { } got && got.ToJsonString() != kv.Value!.ToJsonString())
                    .Select(kv => $"{kv.Key}: sent {Short(kv.Value!.ToJsonString())}, read {Short(EchoTarget(stored, kv.Key)!.ToJsonString())}").ToList();
                if (differences.Count > 0)
                    problem = $"{read.At:HH:mm:ss} {read.Path}: " + string.Join("; ", differences.Take(3));
            }
            results[key] = (r.Support + 1, problem is null ? r.Counter : [.. r.Counter, problem]);
        }
        foreach (var ((createEndpoint, readEndpoint), (support, counter)) in results)
            yield return new ApiLaw
            {
                Endpoint = createEndpoint, Kind = LawKind.RoundTrip, Support = support, Counterexamples = counter,
                Description = counter.Count == 0
                    ? $"Resources created by {createEndpoint} can be read back at {readEndpoint} with the submitted values"
                    : $"Resources created by {createEndpoint} can be read back at {readEndpoint}, except {counter.Count} of {support}",
                Advice = $"Test it with a flow: {createEndpoint} (extract the id) → {readEndpoint} with assertions on the submitted fields."
            };
    }

    /// <summary>After a successful DELETE, reading the same path returns 404 / 410.</summary>
    private static IEnumerable<ApiLaw> DeleteLaws(List<Observation> ordered)
    {
        var results = new Dictionary<string, (int Support, List<string> Counter)>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            var delete = ordered[i];
            if (delete.Method != "DELETE" || !delete.Succeeded)
                continue;
            var later = ordered.Skip(i + 1).TakeWhile(o => !(o.Path == delete.Path && o.Method is "POST" or "PUT"));
            var read = later.FirstOrDefault(o => o.Method == "GET" && o.Path == delete.Path);
            if (read is null)
                continue;
            if (!results.TryGetValue(delete.Endpoint, out var r))
                r = (0, []);
            results[delete.Endpoint] = read.Status is 404 or 410
                ? (r.Support + 1, r.Counter)
                : (r.Support + 1, [.. r.Counter, $"{read.At:HH:mm:ss} GET {read.Path} returned {read.Status} after it was deleted"]);
        }
        foreach (var (endpoint, (support, counter)) in results)
        {
            // Every read succeeding means soft delete by design, not a broken law.
            if (counter.Count == support && support > 1)
                continue;
            yield return new ApiLaw
            {
                Endpoint = endpoint, Kind = LawKind.DeleteGone, Support = support, Counterexamples = counter,
                Description = counter.Count == 0
                    ? $"After {endpoint} succeeds, reading the resource returns 404"
                    : $"After {endpoint} succeeds, reading the resource returns 404, except {counter.Count} of {support}",
                Advice = $"Test it with a flow: {endpoint} → GET the same path, asserting status 404."
            };
        }
    }

    /// <summary>The same GET repeated with no write in between returns the same body, apart from volatile fields.</summary>
    private static IEnumerable<ApiLaw> StableGetLaws(List<Observation> ordered, LawOptions options)
    {
        var pairs = new Dictionary<string, List<(Observation First, Observation Second)>>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            var first = ordered[i];
            if (first.Method != "GET" || !first.Succeeded || Parse(first.ResponseBody) is null)
                continue;
            var root = Resource(first.Path);
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var o = ordered[j];
                if (o.Method is "POST" or "PUT" or "PATCH" or "DELETE" && Resource(o.Path) == root)
                    break;
                if (o.Method == "GET" && o.Url == first.Url)
                {
                    if (!pairs.TryGetValue(first.Endpoint, out var list))
                        pairs[first.Endpoint] = list = [];
                    list.Add((first, o));
                    break;
                }
            }
        }
        foreach (var (endpoint, list) in pairs.Where(p => p.Value.Count >= Math.Max(2, options.MinSamples - 1)))
        {
            var volatilePaths = new SortedSet<string>(StringComparer.Ordinal);
            var counter = new List<string>();
            foreach (var (first, second) in list)
            {
                if (!second.Succeeded)
                {
                    counter.Add($"{second.At:HH:mm:ss} {second.Url} returned {second.Status} after {first.Status} with no write in between");
                    continue;
                }
                foreach (var change in Diff.ResponseDiff.Json(first.ResponseBody, second.ResponseBody, []))
                    volatilePaths.Add(ArrayWildcards().Replace(change.Path, "[*]"));
            }
            var shapeChanged = list.Any(p => p.Second.Succeeded && JsonShape.Diff(p.First.ResponseBody, p.Second.ResponseBody).Any(c => c.Kind != ShapeChangeKind.Added));
            // Items appearing or disappearing, or most fields changing, means the data moves on its own: not "stable".
            var leaves = Math.Max(1, LeafCount(Parse(list[0].First.ResponseBody)));
            if (shapeChanged || volatilePaths.Any(p => p.EndsWith("[*]", StringComparison.Ordinal)) || volatilePaths.Count * 2 >= leaves)
                continue;
            yield return new ApiLaw
            {
                Endpoint = endpoint, Kind = LawKind.StableGet, Support = list.Count, Counterexamples = counter,
                Description = volatilePaths.Count == 0
                    ? $"Repeating {endpoint} returns the same body"
                    : $"Repeating {endpoint} returns the same body apart from " + Fields(volatilePaths),
                Advice = volatilePaths.Count == 0
                    ? "A snapshot assertion fits this endpoint."
                    : $"A snapshot assertion fits this endpoint; ignore {string.Join(", ", volatilePaths)}."
            };
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private sealed record FormatRule(string Name, string Pattern)
    {
        public Regex Regex { get; } = new(Pattern, RegexOptions.CultureInvariant);
    }

    private static readonly FormatRule[] Formats =
    [
        new("UUID", @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"),
        new("date-time", @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?$"),
        new("date", @"^\d{4}-\d{2}-\d{2}$"),
        new("email address", @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
        new("URL", @"^https?://[^\s]+$")
    ];

    private static int Allowed(int count, LawOptions options) =>
        count >= options.MinSamplesForAnomaly ? Math.Max(1, (int)Math.Floor(count * options.AnomalyRate)) : 0;

    private static bool IsFewExceptions(int exceptions, int total, LawOptions options) =>
        exceptions > 0 && total >= options.MinSamplesForAnomaly && exceptions <= Allowed(total, options);

    private static ApiLaw Anomaly(string endpoint, LawKind kind, string description, int support, IEnumerable<string> counterexamples) => new()
    {
        Endpoint = endpoint, Kind = kind, Description = description, Support = support, Counterexamples = counterexamples.Take(10).ToList()
    };

    private static string Where(List<(Observation Observation, JsonNode Json)> samples, int sample) =>
        $"{samples[sample].Observation.At:HH:mm:ss} {samples[sample].Observation.Url}";

    private static string Field(IReadOnlyList<string> container, string key) => JsonShape.Join(container.Append(key).ToList());

    private static bool TopLevel(IReadOnlyList<string> path) => !path.Contains(JsonShape.Any);

    private static string Fields(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 6 ? string.Join(", ", list) : string.Join(", ", list.Take(6)) + $" and {list.Count - 6} more";
    }

    private static string TypeOf(JsonNode? node) => node switch
    {
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null"
        },
        _ => "null"
    };

    private static bool LooksLikeIdentifier(string key) =>
        key.Equals("id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Id", StringComparison.Ordinal) || key.EndsWith("_id", StringComparison.Ordinal)
        || key.Contains("name", StringComparison.OrdinalIgnoreCase) || key.Contains("email", StringComparison.OrdinalIgnoreCase)
        || key.Contains("description", StringComparison.OrdinalIgnoreCase) || key.Contains("title", StringComparison.OrdinalIgnoreCase);

    private static bool IsDate(string s) => Formats[1].Regex.IsMatch(s) || Formats[2].Regex.IsMatch(s);

    private static DateTimeOffset? Date(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && IsDate(v.GetValue<string>())
        && DateTimeOffset.TryParse(v.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private static string? IdOf(JsonObject created)
    {
        static string? Scalar(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
        var target = created["data"] as JsonObject ?? created;
        return Scalar(target["id"]) ?? Scalar(target["_id"]) ?? target.Where(kv => kv.Key.EndsWith("Id", StringComparison.Ordinal)).Select(kv => Scalar(kv.Value)).FirstOrDefault(v => v is not null);
    }

    /// <summary>Where a submitted field shows up in a response: at the root or under a single wrapper (data, result, item…).</summary>
    private static JsonNode? EchoTarget(JsonNode response, string field)
    {
        if (response is not JsonObject obj)
            return null;
        if (obj[field] is { } direct)
            return direct;
        var wrappers = obj.Where(kv => kv.Value is JsonObject).Select(kv => (JsonObject)kv.Value!).ToList();
        return wrappers.Count == 1 ? wrappers[0][field] : null;
    }

    private static IEnumerable<string> TopArrays(JsonNode json) => json switch
    {
        JsonArray => ["$"],
        JsonObject obj => obj.Where(kv => kv.Value is JsonArray).Select(kv => JsonShape.Join([kv.Key])),
        _ => []
    };

    private static JsonArray? ArrayAt(JsonNode json, string path) =>
        path == "$" ? json as JsonArray : json is JsonObject obj ? obj[JsonShape.Split(path)[0]] as JsonArray : null;

    private static double? QueryNumber(string url, string name)
    {
        var q = url.IndexOf('?');
        if (q < 0)
            return null;
        foreach (var part in url[(q + 1)..].Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && Uri.UnescapeDataString(kv[0]) == name
                               && double.TryParse(Uri.UnescapeDataString(kv[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return n;
        }
        return null;
    }

    /// <summary>The collection a path belongs to (<c>/api/users/7</c> → <c>/api/users</c>), to spot writes that affect a GET.</summary>
    private static string Resource(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).TakeWhile(s => !Observations.IsIdSegment(s)).ToList();
        return "/" + string.Join('/', parts);
    }

    private static int LeafCount(JsonNode? node) => node switch
    {
        JsonObject o => o.Sum(kv => LeafCount(kv.Value)),
        JsonArray a => a.Sum(LeafCount),
        _ => 1
    };

    private static JsonNode? Parse(string text)
    {
        var t = text.TrimStart();
        if (!(t.StartsWith('{') || t.StartsWith('[')))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private static string Short(JsonNode? node) => Short(node?.ToJsonString() ?? "null");
    private static string Short(string s) => s.Length > 60 ? s[..57] + "…" : s;
    private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    internal static string Js(string s) => JsonSerializer.Serialize(s);

    [GeneratedRegex("(count|total|size|length|num)", RegexOptions.IgnoreCase)]
    private static partial Regex CountName();

    [GeneratedRegex("(total|count)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalName();

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex ArrayWildcards();
}
