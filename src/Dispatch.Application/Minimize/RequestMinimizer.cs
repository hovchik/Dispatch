using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Minimize;

/// <summary>What has to stay the same for a smaller request to count as reproducing the original.</summary>
public enum OutcomeMatch
{
    /// <summary>Same status code (or, for transport errors, still no response).</summary>
    Status,

    /// <summary>Same status class (2xx, 4xx, ...).</summary>
    StatusClass,

    /// <summary>Same status code and the same assertions / script tests failing.</summary>
    StatusAndTests,

    /// <summary>The body still contains <see cref="MinimizeOptions.BodyContains"/> (status is ignored).</summary>
    BodyContains
}

public sealed class MinimizeOptions
{
    public OutcomeMatch Match { get; init; } = OutcomeMatch.Status;

    /// <summary>Text the body must keep containing, for <see cref="OutcomeMatch.BodyContains"/>.</summary>
    public string? BodyContains { get; init; }

    /// <summary>Upper bound on requests sent (the baseline included). The result is still valid when it's hit, just less minimal.</summary>
    public int MaxRequests { get; init; } = 300;

    /// <summary>Also break the Cookie header and JSON / form bodies into individual cookies, members and fields.</summary>
    public bool Deep { get; init; } = true;

    public bool RunScripts { get; init; } = true;
    public ApiEnvironment? Environment { get; init; }
    public IReadOnlyList<KeyValueItem>? CollectionVariables { get; init; }
}

public enum MinimizeUnitKind
{
    Header,
    Cookie,
    Query,
    Auth,
    Body,
    FormField,
    JsonMember
}

/// <summary>A part of a request that the minimizer can take away. Children are only tried once their parent is known to be needed.</summary>
public sealed class MinimizeUnit
{
    internal MinimizeUnit(MinimizeUnitKind kind, string label, int index = -1, string? name = null, IReadOnlyList<object>? path = null)
    {
        Kind = kind;
        Label = label;
        Index = index;
        Name = name;
        Path = path ?? [];
    }

    public MinimizeUnitKind Kind { get; }

    /// <summary>Position in the request's part list, unique per minimization.</summary>
    internal int Id { get; set; }

    /// <summary>Human-readable, e.g. <c>header Authorization</c>, <c>body $.user.email</c>.</summary>
    public string Label { get; }
    internal int Index { get; }
    internal string? Name { get; }

    /// <summary>JSON path segments (string property names, int array indices) for <see cref="MinimizeUnitKind.JsonMember"/>.</summary>
    internal IReadOnlyList<object> Path { get; }
    internal List<MinimizeUnit> Children { get; } = [];

    public override string ToString() => Label;
}

/// <summary>Progress while minimizing: how many requests went out and the size of the current best request.</summary>
public sealed record MinimizeProgress(int RequestsSent, int Removed, int Remaining, string Message);

public sealed class MinimizeReport
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; set; }
    public required ApiRequest Original { get; init; }

    /// <summary>The smallest request found that still produces <see cref="Outcome"/>.</summary>
    public ApiRequest Minimal { get; set; } = null!;

    /// <summary>The outcome being preserved, e.g. <c>HTTP 403</c>.</summary>
    public string Outcome { get; set; } = string.Empty;
    public OutcomeMatch Match { get; init; }

    /// <summary>Parts that could not be removed without changing the outcome.</summary>
    public List<MinimizeUnit> Required { get; } = [];

    /// <summary>Parts that made no difference.</summary>
    public List<MinimizeUnit> Removed { get; } = [];
    public int RequestsSent { get; set; }

    /// <summary>The request budget ran out before every part was tried, so the result may not be fully minimal.</summary>
    public bool BudgetExhausted { get; set; }

    /// <summary>The minimal request was sent once more at the end and gave the same outcome again.</summary>
    public bool Confirmed { get; set; }

    /// <summary>Set when minimizing could not start (e.g. the baseline did not get a usable outcome).</summary>
    public string? Error { get; set; }

    public int OriginalUnits => Required.Count + Removed.Count;
}

/// <summary>
/// Delta debugging for API requests. Starting from a request with some outcome (a 403, a 500, a failing assertion, a
/// success), it removes headers, cookies, query parameters, auth, form fields and JSON body members — in halves, then in
/// smaller and smaller groups (ddmin), one level of nesting at a time — keeping only the parts the outcome depends on.
///
/// The result answers "what exactly causes this error?" when minimizing a failure, and "what does this endpoint
/// really require?" when minimizing a success. Every probe is a real request, so prefer safe (GET-like) requests or a
/// test environment for endpoints with side effects.
/// </summary>
public sealed class RequestMinimizer(IRequestSender sender)
{
    public async Task<MinimizeReport> MinimizeAsync(ApiRequest request, MinimizeOptions options, IProgress<MinimizeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var original = Normalize(request);
        var report = new MinimizeReport { Original = original, Match = options.Match, Minimal = original.Clone() };
        if (options.Match == OutcomeMatch.BodyContains && string.IsNullOrEmpty(options.BodyContains))
        {
            report.Error = "Enter the text the response body has to keep containing.";
            return report;
        }

        var sendOptions = new SendOptions
        {
            Environment = options.Environment,
            CollectionVariables = options.CollectionVariables,
            RecordHistory = false,
            CheckExpectations = false,
            RunScripts = options.RunScripts,
            Snapshots = Testing.SnapshotMode.Verify
        };

        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var removed = new HashSet<MinimizeUnit>();
        var roots = BuildUnits(original, options.Deep);

        // ---- Baseline ----
        ApiResponse baseline;
        try
        {
            baseline = await sender.SendAsync(original, sendOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Error = "Cancelled.";
            return report;
        }
        report.RequestsSent = 1;
        var target = Signature(baseline, options);
        report.Outcome = Describe(baseline, options);
        if (options.Match == OutcomeMatch.BodyContains && target is null)
        {
            report.Error = $"The original response does not contain \"{options.BodyContains}\", so there is nothing to keep.";
            report.Duration = stopwatch.Elapsed;
            return report;
        }

        async Task<bool> Reproduces(IEnumerable<MinimizeUnit> without)
        {
            var candidate = without.ToHashSet();
            candidate.UnionWith(removed);
            var key = string.Join(',', candidate.Select(u => u.Id).Order());
            if (cache.TryGetValue(key, out var known))
                return known;
            if (report.RequestsSent >= options.MaxRequests)
            {
                report.BudgetExhausted = true;
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var response = await sender.SendAsync(Apply(original, candidate), sendOptions, cancellationToken).ConfigureAwait(false);
            report.RequestsSent++;
            var same = Signature(response, options) == target;
            cache[key] = same;
            return same;
        }

        // ---- Hierarchical ddmin: one level of nesting at a time ----
        var level = roots;
        try
        {
            while (level.Count > 0)
            {
                var keep = await DdMinAsync(level, Reproduces, progress, report, removed, cancellationToken).ConfigureAwait(false);
                foreach (var unit in level.Except(keep))
                    removed.Add(unit);
                level = keep.SelectMany(u => u.Children).ToList();
            }
        }
        catch (OperationCanceledException)
        {
            report.Error = "Cancelled — showing the smallest request found so far.";
        }

        // Anything never reached (its parent was removed) is gone too; everything else that is still present is required.
        var all = Flatten(roots).ToList();
        foreach (var unit in all)
        {
            if (removed.Contains(unit) || HasRemovedAncestor(unit, removed, roots))
                report.Removed.Add(unit);
            else
                report.Required.Add(unit);
        }

        report.Minimal = Apply(original, removed);
        report.Minimal.Name = original.Name + " (minimal)";

        if (report.Error is null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var confirm = await sender.SendAsync(report.Minimal, sendOptions, cancellationToken).ConfigureAwait(false);
                report.RequestsSent++;
                report.Confirmed = Signature(confirm, options) == target;
            }
            catch (OperationCanceledException)
            {
            }
        }

        report.Duration = stopwatch.Elapsed;
        progress?.Report(new MinimizeProgress(report.RequestsSent, report.Removed.Count, report.Required.Count, "Done"));
        return report;
    }

    /// <summary>
    /// Zeller's ddmin over one level: returns a subset of <paramref name="units"/> to keep such that removing any one of
    /// them changes the outcome (1-minimal), using as few requests as it can.
    /// </summary>
    private static async Task<List<MinimizeUnit>> DdMinAsync(List<MinimizeUnit> units, Func<IEnumerable<MinimizeUnit>, Task<bool>> reproduces,
        IProgress<MinimizeProgress>? progress, MinimizeReport report, HashSet<MinimizeUnit> removed, CancellationToken ct)
    {
        var keep = units.ToList();
        if (keep.Count == 0)
            return keep;

        // Parts of this level already dropped stay dropped in every later candidate.
        Task<bool> WithoutAlso(IEnumerable<MinimizeUnit> more) => reproduces(units.Except(keep).Concat(more));

        // Fast path: none of this level matters.
        if (await WithoutAlso(keep).ConfigureAwait(false))
            return [];

        var n = 2;
        while (keep.Count >= 2)
        {
            ct.ThrowIfCancellationRequested();
            var chunks = Split(keep, Math.Min(n, keep.Count));
            var reduced = false;

            // Try removing each chunk (keeping its complement).
            for (var i = 0; i < chunks.Count; i++)
            {
                if (await WithoutAlso(chunks[i]).ConfigureAwait(false))
                {
                    keep = keep.Except(chunks[i]).ToList();
                    n = Math.Max(n - 1, 2);
                    reduced = true;
                    progress?.Report(new MinimizeProgress(report.RequestsSent, removed.Count + units.Count - keep.Count, keep.Count,
                        $"Removed {chunks[i].Count} part(s)"));
                    break;
                }
            }
            if (reduced)
                continue;

            // Try keeping only one chunk (removing everything else at this level).
            if (chunks.Count > 2)
                for (var i = 0; i < chunks.Count; i++)
                {
                    var others = keep.Except(chunks[i]).ToList();
                    if (await WithoutAlso(others).ConfigureAwait(false))
                    {
                        keep = chunks[i];
                        n = 2;
                        reduced = true;
                        break;
                    }
                }
            if (reduced)
                continue;

            if (n >= keep.Count)
                break;
            n = Math.Min(keep.Count, n * 2);
        }

        // A single remaining unit: check whether it is needed at all.
        if (keep.Count == 1 && await WithoutAlso(keep).ConfigureAwait(false))
            keep.Clear();
        return keep;
    }

    private static List<List<MinimizeUnit>> Split(List<MinimizeUnit> units, int parts)
    {
        var result = new List<List<MinimizeUnit>>(parts);
        var start = 0;
        for (var i = 0; i < parts; i++)
        {
            var size = (units.Count - start) / (parts - i);
            result.Add(units.GetRange(start, size));
            start += size;
        }
        return result;
    }

    // ---- Outcomes ----------------------------------------------------------------------------

    /// <summary>A comparable fingerprint of a response under the chosen match rule (null = "no match" for BodyContains).</summary>
    internal static string? Signature(ApiResponse response, MinimizeOptions options)
    {
        if (!response.HasResponse)
            return options.Match == OutcomeMatch.BodyContains ? null : "no-response";
        return options.Match switch
        {
            OutcomeMatch.StatusClass => $"{response.StatusCode / 100}xx",
            OutcomeMatch.StatusAndTests => $"{response.StatusCode}|" +
                                          string.Join(',', response.TestResults.Where(t => !t.Passed).Select(t => t.Name).Order(StringComparer.Ordinal)),
            OutcomeMatch.BodyContains => response.Body.Contains(options.BodyContains!, StringComparison.Ordinal) ? "contains" : null,
            _ => response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static string Describe(ApiResponse response, MinimizeOptions options)
    {
        if (!response.HasResponse)
            return $"no response ({response.Error})";
        var status = $"HTTP {response.StatusCode}{(string.IsNullOrEmpty(response.ReasonPhrase) ? "" : " " + response.ReasonPhrase)}";
        return options.Match switch
        {
            OutcomeMatch.StatusClass => $"{response.StatusCode / 100}xx ({status})",
            OutcomeMatch.StatusAndTests => response.TestResults.Any(t => !t.Passed)
                ? $"{status} with failing: {string.Join(", ", response.TestResults.Where(t => !t.Passed).Select(t => t.Name))}"
                : $"{status} with all tests passing",
            OutcomeMatch.BodyContains => $"body contains \"{options.BodyContains}\"",
            _ => status
        };
    }

    // ---- Units -------------------------------------------------------------------------------

    /// <summary>Copies the request and moves a URL-only query string into the Params table, so params can be removed one by one.</summary>
    private static ApiRequest Normalize(ApiRequest request)
    {
        var copy = request.Clone();
        if (copy.QueryParams.Count == 0)
            copy.QueryParams = QueryString.Parse(copy.Url);
        return copy;
    }

    /// <summary>How many top-level parts (headers, params, auth, body) the minimizer would try to remove.</summary>
    public static int CountParts(ApiRequest request) => BuildUnits(Normalize(request), deep: false).Count;

    internal static List<MinimizeUnit> BuildUnits(ApiRequest request, bool deep)
    {
        var units = new List<MinimizeUnit>();
        for (var i = 0; i < request.Headers.Count; i++)
        {
            var header = request.Headers[i];
            if (!header.IsActive)
                continue;
            var unit = new MinimizeUnit(MinimizeUnitKind.Header, $"header {header.Key}", i);
            if (deep && header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                foreach (var name in CookieNames(header.Value))
                    unit.Children.Add(new MinimizeUnit(MinimizeUnitKind.Cookie, $"cookie {name}", i, name));
            if (unit.Children.Count == 1)
                unit.Children.Clear();
            units.Add(unit);
        }
        for (var i = 0; i < request.QueryParams.Count; i++)
            if (request.QueryParams[i].IsActive)
                units.Add(new MinimizeUnit(MinimizeUnitKind.Query, $"query {request.QueryParams[i].Key}", i));
        if (request.Auth.Mode != AuthMode.None)
            units.Add(new MinimizeUnit(MinimizeUnitKind.Auth, $"auth ({request.Auth.Mode})"));

        if (request.Body.Mode != BodyMode.None)
        {
            if (request.Body.Mode is BodyMode.FormUrlEncoded or BodyMode.Multipart)
            {
                var body = new MinimizeUnit(MinimizeUnitKind.Body, "body");
                if (deep)
                    for (var i = 0; i < request.Body.FormFields.Count; i++)
                        if (request.Body.FormFields[i].IsActive)
                            body.Children.Add(new MinimizeUnit(MinimizeUnitKind.FormField, $"form {request.Body.FormFields[i].Key}", i));
                units.Add(body);
            }
            else if (request.Body.Mode == BodyMode.Json)
            {
                var body = new MinimizeUnit(MinimizeUnitKind.Body, "body");
                if (deep && TryParseJson(request.Body.Content) is { } root)
                    AddJsonChildren(body, root, []);
                units.Add(body);
            }
            else
                units.Add(new MinimizeUnit(MinimizeUnitKind.Body, "body"));
        }

        var id = 0;
        foreach (var unit in Flatten(units))
            unit.Id = id++;
        return units;
    }

    private static void AddJsonChildren(MinimizeUnit parent, JsonNode node, List<object> path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    var childPath = new List<object>(path) { key };
                    var child = new MinimizeUnit(MinimizeUnitKind.JsonMember, "body " + JsonPathText(childPath), path: childPath);
                    if (value is not null)
                        AddJsonChildren(child, value, childPath);
                    parent.Children.Add(child);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var childPath = new List<object>(path) { i };
                    var child = new MinimizeUnit(MinimizeUnitKind.JsonMember, "body " + JsonPathText(childPath), path: childPath);
                    if (array[i] is { } value)
                        AddJsonChildren(child, value, childPath);
                    parent.Children.Add(child);
                }
                break;
        }
    }

    internal static string JsonPathText(IReadOnlyList<object> path)
    {
        var sb = new System.Text.StringBuilder("$");
        foreach (var segment in path)
            sb.Append(segment is int i ? $"[{i}]"
                : IsIdentifier((string)segment) ? "." + segment : $"['{((string)segment).Replace("'", "\\'")}']");
        return sb.ToString();
    }

    private static bool IsIdentifier(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> CookieNames(string header) =>
        header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split('=', 2)[0].Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

    private static IEnumerable<MinimizeUnit> Flatten(IEnumerable<MinimizeUnit> units) =>
        units.SelectMany(u => new[] { u }.Concat(Flatten(u.Children)));

    private static bool HasRemovedAncestor(MinimizeUnit unit, HashSet<MinimizeUnit> removed, List<MinimizeUnit> roots)
    {
        bool Search(IEnumerable<MinimizeUnit> level, bool ancestorRemoved)
        {
            foreach (var u in level)
            {
                if (u == unit)
                    return ancestorRemoved;
                if (Search(u.Children, ancestorRemoved || removed.Contains(u)))
                    return true;
            }
            return false;
        }
        return Search(roots, false);
    }

    /// <summary>Builds the request with the given parts taken out.</summary>
    internal static ApiRequest Apply(ApiRequest original, IReadOnlySet<MinimizeUnit> removed)
    {
        var request = original.Clone();
        foreach (var unit in removed)
        {
            switch (unit.Kind)
            {
                case MinimizeUnitKind.Header:
                    request.Headers[unit.Index].Enabled = false;
                    break;
                case MinimizeUnitKind.Query:
                    request.QueryParams[unit.Index].Enabled = false;
                    break;
                case MinimizeUnitKind.Auth:
                    request.Auth = new AuthSettings();
                    break;
                case MinimizeUnitKind.Body:
                    request.Body = new RequestBody();
                    break;
                case MinimizeUnitKind.FormField when request.Body.FormFields.Count > unit.Index:
                    request.Body.FormFields[unit.Index].Enabled = false;
                    break;
            }
        }

        // Cookies: rebuild the header without the removed ones.
        foreach (var group in removed.Where(u => u.Kind == MinimizeUnitKind.Cookie).GroupBy(u => u.Index))
        {
            var header = request.Headers[group.Key];
            var drop = group.Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
            header.Value = string.Join("; ", header.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(c => !drop.Contains(c.Split('=', 2)[0].Trim())));
        }

        // JSON members: copy the original tree, skipping removed paths.
        var jsonRemoved = removed.Where(u => u.Kind == MinimizeUnitKind.JsonMember).Select(u => JsonPathText(u.Path)).ToHashSet(StringComparer.Ordinal);
        if (jsonRemoved.Count > 0 && request.Body.Mode == BodyMode.Json && TryParseJson(original.Body.Content) is { } root)
        {
            var pretty = original.Body.Content.Contains('\n');
            request.Body.Content = Copy(root, [], jsonRemoved)?.ToJsonString(new JsonSerializerOptions { WriteIndented = pretty }) ?? "";
        }
        return request;
    }

    private static JsonNode? Copy(JsonNode? node, List<object> path, HashSet<string> removed) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .Where(kv => !removed.Contains(JsonPathText([.. path, kv.Key])))
            .Select(kv => KeyValuePair.Create(kv.Key, Copy(kv.Value, [.. path, kv.Key], removed)))),
        JsonArray array => new JsonArray(array
            .Select((item, i) => (item, i))
            .Where(x => !removed.Contains(JsonPathText([.. path, x.i])))
            .Select(x => Copy(x.item, [.. path, x.i], removed)).ToArray()),
        _ => node?.DeepClone()
    };
}
