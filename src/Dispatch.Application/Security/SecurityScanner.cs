using System.Diagnostics;
using System.Text.RegularExpressions;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Security;

/// <summary>
/// Runs a security scan against requests in a collection using the normal sending pipeline. It sends one baseline
/// request per endpoint (for passive checks) and, when active scanning is on, a bounded set of probes that vary one
/// input at a time, comparing each response against the baseline to spot missing validation and error handling.
///
/// This is a testing tool for APIs you are authorised to test (your own, or ones you have permission for). It reports
/// weaknesses; it does not exploit them.
/// </summary>
public sealed partial class SecurityScanner(IRequestSender sender)
{
    public async Task<ScanReport> ScanAsync(IReadOnlyList<ApiRequest> requests, ScanOptions options, ApiEnvironment? environment,
        IReadOnlyList<KeyValueItem>? collectionVariables = null, IProgress<ScanFinding>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ScanReport();
        var stopwatch = Stopwatch.StartNew();

        foreach (var request in requests.Where(Scannable))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var sendOptions = new SendOptions
            {
                Environment = environment,
                CollectionVariables = collectionVariables,
                RecordHistory = false,
                RunScripts = false
            };

            ApiResponse baseline;
            try
            {
                baseline = await sender.SendAsync(request, sendOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            report.RequestsScanned++;

            if (options.Passive)
                foreach (var finding in PassiveChecks.Inspect(request, baseline))
                    Record(report, progress, finding);

            if (!options.Active || !baseline.HasResponse)
                continue;

            foreach (var probe in Probes.ForRequest(request, options))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                ApiResponse response;
                try
                {
                    response = await sender.SendAsync(probe.Request, sendOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                report.ProbesSent++;
                if (Analyze(request, probe, baseline, response) is { } finding)
                    Record(report, progress, finding);
            }
        }

        report.Duration = stopwatch.Elapsed;
        return report;
    }

    private static void Record(ScanReport report, IProgress<ScanFinding>? progress, ScanFinding finding)
    {
        // De-duplicate identical findings that several probes on the same field can raise.
        if (report.Findings.Any(f => f.Title == finding.Title && f.RequestName == finding.RequestName && f.Category == finding.Category
                                     && f.Evidence == finding.Evidence))
            return;
        report.Findings.Add(finding);
        progress?.Report(finding);
    }

    /// <summary>Judges a probe response against the baseline.</summary>
    private static ScanFinding? Analyze(ApiRequest request, Probe probe, ApiResponse baseline, ApiResponse response)
    {
        if (!response.HasResponse)
            return null;

        switch (probe.Kind)
        {
            case ProbeKind.AuthBypass:
                // If removing credentials still yields a successful, non-empty response, the endpoint isn't enforcing auth.
                if (response.IsSuccess && baseline.IsSuccess && response.StatusCode is >= 200 and < 300)
                    return new ScanFinding(ScanSeverity.High, ScanCategory.Authentication, "Endpoint responds without authentication",
                        "The same request without its Authorization header or cookies still returned a successful response, so the endpoint may not require authentication.",
                        $"HTTP {response.StatusCode} without credentials", "Reject unauthenticated requests with 401, and verify authorization on every endpoint.", request.Name);
                return null;

            case ProbeKind.Reflection:
                // The benign marker echoed back unencoded in an HTML response indicates output isn't encoded (XSS risk).
                if ((response.ContentType ?? "").Contains("html", StringComparison.OrdinalIgnoreCase)
                    && response.Body.Contains(Probes.ReflectionMarker, StringComparison.Ordinal))
                    return new ScanFinding(ScanSeverity.High, ScanCategory.Injection, "Input reflected unencoded in an HTML response",
                        $"A marker sent in {probe.Location} '{probe.Field}' was returned verbatim in an HTML response, so unescaped input can inject markup or script (reflected XSS).",
                        probe.Payload, "HTML-encode user input on output, and set a Content-Security-Policy.", request.Name);
                return null;

            case ProbeKind.ErrorSignature:
                // A crafted quote/terminator that triggers a parser/database error signals missing input validation.
                var signature = ErrorSignature(response.Body);
                if (signature is not null && response.StatusCode >= 500 && !(baseline.StatusCode >= 500))
                    return new ScanFinding(ScanSeverity.High, ScanCategory.Injection, $"{signature} error triggered by crafted input",
                        $"Sending '{probe.Payload}' in {probe.Location} '{probe.Field}' produced a {signature} error ({response.StatusCode}), which indicates the input reaches a parser or query without validation.",
                        Snippet(response.Body), "Use parameterised queries / safe parsers and validate input; never build queries by string concatenation.", request.Name);
                if (signature is not null && !(baseline.Body.Length > 0 && ErrorSignature(baseline.Body) == signature))
                    return new ScanFinding(ScanSeverity.Medium, ScanCategory.Injection, $"{signature} error surfaced to the client",
                        $"Crafted input in {probe.Location} '{probe.Field}' produced a {signature} error message in the response body.",
                        Snippet(response.Body), "Validate input and return a generic error; log details server-side.", request.Name);
                return null;

            case ProbeKind.Boundary:
                if (response.StatusCode >= 500 && !(baseline.StatusCode >= 500))
                    return new ScanFinding(ScanSeverity.Medium, ScanCategory.InputValidation, "Unhandled server error on boundary input",
                        $"An out-of-range or malformed value ({Describe(probe.Payload)}) in {probe.Location} '{probe.Field}' caused HTTP {response.StatusCode}, where the baseline succeeded. The input isn't validated before use.",
                        $"payload {Describe(probe.Payload)} → HTTP {response.StatusCode}", "Validate type, range and length, and handle invalid input with a 4xx status.", request.Name);
                return null;

            default:
                return null;
        }
    }

    /// <summary>Names the kind of parser/database error a body reveals, or null.</summary>
    private static string? ErrorSignature(string body)
    {
        if (body.Length == 0)
            return null;
        var sample = body.Length > 6000 ? body[..6000] : body;
        if (SqlErrorRegex().IsMatch(sample))
            return "SQL";
        if (sample.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase) && sample.Contains("JSON", StringComparison.OrdinalIgnoreCase))
            return "JSON parser";
        if (Regex.IsMatch(sample, @"(XML|SAX|DOMDocument).{0,40}(error|exception)", RegexOptions.IgnoreCase))
            return "XML parser";
        if (Regex.IsMatch(sample, @"(NoSQL|MongoError|BSON)", RegexOptions.IgnoreCase))
            return "NoSQL";
        return null;
    }

    private static bool Scannable(ApiRequest request) =>
        request.Kind is RequestKind.Http or RequestKind.GraphQl or RequestKind.Soap && request.Url.Trim().Length > 0;

    private static string Describe(string payload) =>
        payload.Length > 24 ? $"a {payload.Length}-character value" : payload.Length == 0 ? "an empty value" : $"'{payload}'";

    private static string Snippet(string body)
    {
        var single = Regex.Replace(body, @"\s+", " ").Trim();
        return single.Length > 200 ? single[..197] + "…" : single;
    }

    [GeneratedRegex(@"(SQL syntax|SQLSTATE|ORA-\d{5}|psql:|PG::|near "".*"": syntax error|unclosed quotation mark|SQLite3?::|MySqlException|PostgresException|SqlException|You have an error in your SQL)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlErrorRegex();
}
