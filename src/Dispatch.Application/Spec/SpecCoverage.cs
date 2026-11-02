using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Domain;

namespace Dispatch.Application.Spec;

/// <summary>
/// One HTTP exchange (or one saved request) to score against a spec. <see cref="Statuses"/> holds the observed status for
/// real traffic, or the statuses a saved request asserts / has examples for when scoring a collection as defined.
/// </summary>
public sealed record CoverageSample(
    string? Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    IReadOnlyList<int> Statuses,
    string Source)
{
    public static CoverageSample Exchange(string method, string url, int status, string source = "traffic",
        IReadOnlyList<KeyValuePair<string, string>>? headers = null, string? body = null) =>
        new(method, url, QueryString.Parse(url).Select(q => KeyValuePair.Create(q.Key, q.Value)).ToList(), headers ?? [], body, [status], source);
}

/// <summary>Builds coverage samples from the places Dispatch keeps requests and traffic.</summary>
public static class CoverageSamples
{
    /// <summary>The HTTP requests of a collection as defined: URL with variables resolved where known, enabled params and headers, asserted statuses.</summary>
    public static IReadOnlyList<CoverageSample> FromCollection(RequestCollection collection, ApiEnvironment? environment = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in collection.Variables.Where(v => v.IsActive))
            variables[v.Key.Trim()] = v.Value;
        foreach (var (k, v) in environment?.ToDictionary() ?? new Dictionary<string, string>())
            variables[k] = v;
        string Resolve(string text) => Regex.Replace(text ?? "", @"\{\{\s*([^}]+?)\s*\}\}",
            m => variables.TryGetValue(m.Groups[1].Value, out var value) && value.Length > 0 ? value : m.Value);

        var list = new List<CoverageSample>();
        foreach (var request in collection.Requests.Where(r => r.Kind == RequestKind.Http).OrderBy(r => r.SortOrder))
        {
            var url = Resolve(request.Url);
            var query = QueryString.Parse(url).Select(q => KeyValuePair.Create(q.Key, Resolve(q.Value))).ToList();
            foreach (var p in request.QueryParams.Where(p => p.Enabled && p.Key.Length > 0 && !query.Any(q => q.Key == p.Key)))
                query.Add(KeyValuePair.Create(p.Key, Resolve(p.Value)));
            var headers = request.Headers.Where(h => h.Enabled && h.Key.Length > 0).Select(h => KeyValuePair.Create(h.Key, Resolve(h.Value))).ToList();
            if (request.Auth.Mode == AuthMode.ApiKey && request.Auth.ApiKeyLocation == ApiKeyLocation.Header)
                headers.Add(KeyValuePair.Create(request.Auth.ApiKeyName, "***"));
            else if (request.Auth.Mode is AuthMode.Bearer or AuthMode.Basic or AuthMode.OAuth2 or AuthMode.Digest)
                headers.Add(KeyValuePair.Create("Authorization", "***"));
            var body = request.Body.Mode is BodyMode.Json or BodyMode.Text or BodyMode.Xml ? Resolve(request.Body.Content)
                : request.Body.Mode is BodyMode.FormUrlEncoded or BodyMode.Multipart
                    ? JsonSerializer.Serialize(request.Body.FormFields.Where(f => f.Enabled).ToDictionary(f => f.Key, f => (object?)Resolve(f.Value)))
                    : null;

            var statuses = new List<int>();
            foreach (var a in request.Assertions.Where(a => a.Enabled && a.Source == ValueSource.Status && a.Operator == AssertionOperator.Equals))
                if (int.TryParse(a.Expected.Trim(), out var status))
                    statuses.Add(status);
            statuses.AddRange(request.Examples.Select(e => e.StatusCode).Where(s => s > 0));
            list.Add(new CoverageSample(request.Method.ToString().ToUpperInvariant(), QueryString.Split(url).BaseUrl, query, headers, body,
                statuses.Distinct().ToList(), request.Name));
        }
        return list;
    }

    /// <summary>Results of a live run (every HTTP request that got a response), with the raw request's headers and body.</summary>
    public static IReadOnlyList<CoverageSample> FromRun(RunReport report)
    {
        var list = new List<CoverageSample>();
        foreach (var r in report.Results.Where(r => r.Request.Kind == RequestKind.Http && r.Response.HasResponse))
        {
            var url = r.Response.EffectiveUrl ?? r.Request.Url;
            var (headers, body) = ParseRaw(r.Response.RawRequest);
            if (headers.Count == 0)
                headers = r.Request.Headers.Where(h => h.Enabled).Select(h => KeyValuePair.Create(h.Key, h.Value)).ToList();
            list.Add(new CoverageSample(r.Request.Method.ToString().ToUpperInvariant(), QueryString.Split(url).BaseUrl,
                QueryString.Parse(url).Select(q => KeyValuePair.Create(q.Key, q.Value)).ToList(), headers, body, [r.Response.StatusCode], r.Name));
        }
        return list;
    }

    /// <summary>
    /// A <c>dispatch run -r json</c> report: URL, status and name per result (no bodies or headers). With the collection the run
    /// came from, the method, headers and body are taken from the request of the same name.
    /// </summary>
    public static IReadOnlyList<CoverageSample> FromRunJson(JsonNode root, RequestCollection? collection = null)
    {
        var list = new List<CoverageSample>();
        foreach (var result in root["results"] as JsonArray ?? [])
        {
            if (result is null || result["kind"]?.ToString() is { } kind && kind != "Http")
                continue;
            if (result["status"]?.GetValue<int>() is not int status || status <= 0)
                continue;
            var url = result["url"]?.ToString() ?? "";
            var name = result["name"]?.ToString() ?? "";
            var request = collection?.Requests.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var body = request?.Body.Mode is BodyMode.Json or BodyMode.Text ? request.Body.Content : null;
            list.Add(new CoverageSample(request?.Method.ToString().ToUpperInvariant(), QueryString.Split(url).BaseUrl,
                QueryString.Parse(url).Select(q => KeyValuePair.Create(q.Key, q.Value)).ToList(),
                request?.Headers.Where(h => h.Enabled).Select(h => KeyValuePair.Create(h.Key, h.Value)).ToList() ?? [], body, [status], name));
        }
        return list;
    }

    public static bool IsRunJson(JsonNode? root) => root is JsonObject o && o["results"] is JsonArray && o["totals"] is not null;

    /// <summary>Entries of a HAR file (browser devtools or the capture proxy's export).</summary>
    public static IReadOnlyList<CoverageSample> FromHar(JsonNode root)
    {
        var list = new List<CoverageSample>();
        foreach (var entry in root["log"]?["entries"]?.AsArray() ?? [])
        {
            var request = entry?["request"];
            var response = entry?["response"];
            if (request is null || response is null || response["status"]?.GetValue<int>() is not int status || status <= 0)
                continue;
            var url = request["url"]?.ToString() ?? "";
            var query = (request["queryString"] as JsonArray ?? []).Select(q => KeyValuePair.Create(q?["name"]?.ToString() ?? "", q?["value"]?.ToString() ?? "")).ToList();
            if (query.Count == 0)
                query = QueryString.Parse(url).Select(q => KeyValuePair.Create(q.Key, q.Value)).ToList();
            var headers = (request["headers"] as JsonArray ?? []).Select(h => KeyValuePair.Create(h?["name"]?.ToString() ?? "", h?["value"]?.ToString() ?? ""))
                .Where(h => h.Key.Length > 0 && !h.Key.StartsWith(':')).ToList();
            list.Add(new CoverageSample(request["method"]?.ToString()?.ToUpperInvariant() ?? "GET", QueryString.Split(url).BaseUrl, query, headers,
                request["postData"]?["text"]?.ToString(), [status], "HAR"));
        }
        return list;
    }

    /// <summary>Headers and body of a raw HTTP/1.1 request text.</summary>
    internal static (List<KeyValuePair<string, string>> Headers, string? Body) ParseRaw(string? raw)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(raw))
            return (headers, null);
        var text = raw.Replace("\r\n", "\n");
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        foreach (var line in head.Split('\n').Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers.Add(KeyValuePair.Create(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        var body = split < 0 ? null : text[(split + 2)..];
        return (headers, string.IsNullOrWhiteSpace(body) ? null : body);
    }
}

public enum CoverageMetricId
{
    Operations,
    StatusCodes,
    Parameters,
    RequiredFields,
    Documented
}

/// <summary>Something the spec documents that the traffic never exercised (or, for <see cref="CoverageMetricId.Documented"/>, a call the spec lacks).</summary>
public sealed record CoverageGap(string Operation, string Detail, string? Hint = null);

/// <summary>One coverage dimension: how many of the documented items were exercised, with the ones that were not.</summary>
public sealed record CoverageMetric(CoverageMetricId Id, string Name, string Description, int Covered, int Total, IReadOnlyList<CoverageGap> Gaps)
{
    /// <summary>100 when there is nothing to cover.</summary>
    public double Percent => Total == 0 ? 100 : Math.Round(100.0 * Covered / Total, 1);
    public string PercentText => $"{Percent:0.#}%";
    public string Ratio => $"{Covered}/{Total}";
}

/// <summary>Per-operation summary row.</summary>
public sealed record OperationCoverage(SpecOperation Operation, int Calls, IReadOnlyList<int> StatusesSeen, int StatusesDocumented, int ParametersVaried,
    int ParametersTotal, bool Deprecated)
{
    public string Key => Operation.Key;
    public string StatusesText => StatusesSeen.Count == 0 ? "—" : string.Join(", ", StatusesSeen.OrderBy(s => s));
    public bool Called => Calls > 0;
}

public sealed class CoverageReport
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required string SpecTitle { get; init; }
    public required string Source { get; init; }
    public int SampleCount { get; init; }
    public IReadOnlyList<CoverageMetric> Metrics { get; init; } = [];
    public IReadOnlyList<OperationCoverage> Operations { get; init; } = [];

    /// <summary>Distinct undocumented endpoints seen in the traffic (method + path template).</summary>
    public IReadOnlyList<CoverageGap> Undocumented { get; init; } = [];

    public CoverageMetric this[CoverageMetricId id] => Metrics.First(m => m.Id == id);

    /// <summary>Mean of the metric percentages (metrics with nothing to cover are left out).</summary>
    public double Overall
    {
        get
        {
            var counted = Metrics.Where(m => m.Total > 0).ToList();
            return counted.Count == 0 ? 100 : Math.Round(counted.Average(m => m.Percent), 1);
        }
    }

    public bool Passes(double minimum) => Overall >= minimum;

    public string Summary
    {
        get
        {
            var ops = this[CoverageMetricId.Operations];
            var undocumented = Undocumented.Count;
            return $"{Overall:0.#}% spec coverage: {ops.Covered} of {ops.Total} operations called, " +
                   $"{this[CoverageMetricId.StatusCodes].Covered} of {this[CoverageMetricId.StatusCodes].Total} documented status codes seen, " +
                   $"{this[CoverageMetricId.Parameters].Covered} of {this[CoverageMetricId.Parameters].Total} parameters varied, " +
                   $"{this[CoverageMetricId.RequiredFields].Covered} of {this[CoverageMetricId.RequiredFields].Total} required inputs omitted at least once" +
                   (undocumented > 0 ? $"; {undocumented} undocumented endpoint(s) called." : ".");
        }
    }
}

/// <summary>
/// Spec coverage: how much of an OpenAPI document a collection, a run or captured traffic exercises. Five metrics, each a
/// percentage with the gaps listed: operations never called, documented status codes never seen, parameters never varied,
/// required inputs never omitted (no negative test), and calls the spec does not document.
/// </summary>
public static class SpecCoverage
{
    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept", "accept-encoding", "accept-language", "cache-control", "connection", "content-length", "content-type", "cookie", "host",
        "origin", "pragma", "referer", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform", "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site",
        "user-agent", "x-requested-with", "if-none-match", "if-modified-since", "upgrade-insecure-requests", "te", "priority"
    };

    public static CoverageReport Analyze(SpecDocument spec, IReadOnlyList<CoverageSample> samples, string source)
    {
        // Pair every sample with its operation.
        var calls = new Dictionary<SpecOperation, List<CoverageSample>>();
        foreach (var op in spec.Operations)
            calls[op] = [];
        var undocumented = new Dictionary<string, (int Count, string? Hint)>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var (operation, template) = spec.Match(sample.Method, sample.Url);
            if (operation is not null)
            {
                calls[operation].Add(sample);
                continue;
            }
            var key = $"{sample.Method ?? "?"} {Laws.Observations.PathTemplate(sample.Url)}";
            var hint = template is not null ? $"{template} is documented, but not for {sample.Method}." : null;
            undocumented[key] = undocumented.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.Hint ?? hint) : (1, hint);
        }

        // 1. Operations
        var operationGaps = spec.Operations.Where(o => calls[o].Count == 0)
            .Select(o => new CoverageGap(o.Key, "never called", o.Deprecated ? "deprecated" : o.Summary)).ToList();
        var operations = new CoverageMetric(CoverageMetricId.Operations, "Operations called", "Documented operations that were called at least once.",
            spec.Operations.Count - operationGaps.Count, spec.Operations.Count, operationGaps);

        // 2. Status codes
        var statusGaps = new List<CoverageGap>();
        var statusTotal = 0;
        var statusCovered = 0;
        foreach (var op in spec.Operations)
        {
            var seen = calls[op].SelectMany(s => s.Statuses).Distinct().ToList();
            foreach (var response in op.ConcreteResponses)
            {
                statusTotal++;
                if (seen.Any(response.Matches))
                    statusCovered++;
                else
                    statusGaps.Add(new CoverageGap(op.Key, $"{response.Code} never seen", seen.Count == 0 ? null : $"seen: {string.Join(", ", seen.OrderBy(s => s))}"));
            }
        }
        var statuses = new CoverageMetric(CoverageMetricId.StatusCodes, "Status codes seen", "Documented responses (per operation) that were observed.",
            statusCovered, statusTotal, statusGaps);

        // 3. Parameters varied
        var parameterGaps = new List<CoverageGap>();
        var parameterTotal = 0;
        var parameterCovered = 0;
        var variedPerOp = new Dictionary<SpecOperation, int>();
        foreach (var op in spec.Operations)
        {
            variedPerOp[op] = 0;
            foreach (var parameter in op.Parameters.Where(p => p.In is "path" or "query" or "header" or "cookie"))
            {
                parameterTotal++;
                var values = calls[op].Select(s => ValueOf(spec, op, parameter, s)).ToList();
                var present = values.Where(v => v is not null).Distinct(StringComparer.Ordinal).ToList();
                var omitted = values.Count(v => v is null);
                var varied = present.Count >= 2 || (present.Count == 1 && omitted > 0 && !parameter.Required);
                if (varied)
                {
                    parameterCovered++;
                    variedPerOp[op]++;
                }
                else
                {
                    parameterGaps.Add(new CoverageGap(op.Key, $"{parameter.In} parameter '{parameter.Name}' " +
                        (present.Count == 0 ? "never sent" : $"always {Quote(present[0])}"),
                        parameter.Enum.Count > 0 ? $"enum: {string.Join(", ", parameter.Enum.Take(6))}" : parameter.Type));
                }
            }
        }
        var parameters = new CoverageMetric(CoverageMetricId.Parameters, "Parameters varied", "Path, query and header parameters sent with at least two different values (or present and absent when optional).",
            parameterCovered, parameterTotal, parameterGaps);

        // 4. Required inputs omitted at least once (negative tests)
        var requiredGaps = new List<CoverageGap>();
        var requiredTotal = 0;
        var requiredCovered = 0;
        foreach (var op in spec.Operations)
        {
            var requiredFields = SchemaShape.RequiredOf(spec.Root, op.RequestSchema);
            foreach (var field in requiredFields)
            {
                requiredTotal++;
                var bodies = calls[op].Select(s => ParseBody(s.Body)).ToList();
                if (bodies.Any(b => b is JsonObject o && !o.ContainsKey(field)) || (op.RequestBodyRequired && calls[op].Any(s => string.IsNullOrWhiteSpace(s.Body))))
                    requiredCovered++;
                else
                    requiredGaps.Add(new CoverageGap(op.Key, $"required body field '{field}' never omitted", "no negative test"));
            }
            foreach (var parameter in op.Parameters.Where(p => p.Required && p.In is "query" or "header"))
            {
                requiredTotal++;
                if (calls[op].Any(s => ValueOf(spec, op, parameter, s) is null))
                    requiredCovered++;
                else
                    requiredGaps.Add(new CoverageGap(op.Key, $"required {parameter.In} parameter '{parameter.Name}' never omitted", "no negative test"));
            }
        }
        var required = new CoverageMetric(CoverageMetricId.RequiredFields, "Required inputs omitted", "Required body fields and parameters that were left out in at least one call, so the 4xx path is tested.",
            requiredCovered, requiredTotal, requiredGaps);

        // 5. Documented traffic
        var undocumentedCalls = undocumented.Values.Sum(v => v.Count);
        var undocumentedGaps = undocumented.OrderByDescending(u => u.Value.Count)
            .Select(u => new CoverageGap(u.Key, $"not in the spec ({u.Value.Count} call{(u.Value.Count == 1 ? "" : "s")})", u.Value.Hint)).ToList();
        var documented = new CoverageMetric(CoverageMetricId.Documented, "Calls documented", "Calls in the traffic that match an operation of the spec.",
            samples.Count - undocumentedCalls, samples.Count, undocumentedGaps);

        var rows = spec.Operations.Select(op => new OperationCoverage(op, calls[op].Count,
            calls[op].SelectMany(s => s.Statuses).Distinct().ToList(), op.ConcreteResponses.Count(), variedPerOp[op],
            op.Parameters.Count(p => p.In is "path" or "query" or "header" or "cookie"), op.Deprecated)).ToList();

        return new CoverageReport
        {
            SpecTitle = spec.Title + (spec.Version.Length > 0 ? $" {spec.Version}" : ""),
            Source = source,
            SampleCount = samples.Count,
            Metrics = [operations, statuses, parameters, required, documented],
            Operations = rows,
            Undocumented = undocumentedGaps
        };
    }

    /// <summary>The value a sample sends for a parameter, or null when it is absent.</summary>
    private static string? ValueOf(SpecDocument spec, SpecOperation op, SpecParameter parameter, CoverageSample sample)
    {
        switch (parameter.In)
        {
            case "path":
                return spec.PathValues(op, sample.Url).TryGetValue(parameter.Name, out var v) ? v : null;
            case "query":
                var q = sample.Query.Where(x => x.Key.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToList();
                return q.Count == 0 ? null : string.Join(",", q);
            case "header":
                return sample.Headers.FirstOrDefault(h => h.Key.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)) is { Key.Length: > 0 } h ? h.Value : null;
            case "cookie":
                var cookie = sample.Headers.FirstOrDefault(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value;
                var match = cookie is null ? null : Regex.Match(cookie, @"(?:^|;\s*)" + Regex.Escape(parameter.Name) + "=([^;]*)");
                return match is { Success: true } ? match.Groups[1].Value : null;
            default:
                return null;
        }
    }

    private static JsonNode? ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Quote(string value) => value.Length > 40 ? $"'{value[..37]}…'" : $"'{value}'";

    /// <summary>Request headers worth reporting as "sent but undocumented" (not browser / transport noise).</summary>
    internal static bool IsInteresting(string header) => !IgnoredHeaders.Contains(header);
}
