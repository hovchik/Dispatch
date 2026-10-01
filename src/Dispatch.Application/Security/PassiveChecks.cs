using System.Text.RegularExpressions;
using Dispatch.Domain;

namespace Dispatch.Application.Security;

/// <summary>
/// Passive security checks: they read a normal request and its response and never send crafted input. Covers transport
/// security, response security headers, information disclosure (server banners, stack traces, secrets in bodies) and
/// permissive CORS.
/// </summary>
public static partial class PassiveChecks
{
    public static IEnumerable<ScanFinding> Inspect(ApiRequest request, ApiResponse response)
    {
        var findings = new List<ScanFinding>();
        if (!response.HasResponse)
            return findings;

        var name = request.Name;
        var headers = response.Headers
            .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        var url = response.EffectiveUrl ?? request.Url;
        var isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        void Add(ScanSeverity severity, ScanCategory category, string title, string detail, string evidence = "", string remediation = "") =>
            findings.Add(new ScanFinding(severity, category, title, detail, evidence, remediation, name));

        // ---- Transport security ----
        if (isHttp)
            Add(ScanSeverity.High, ScanCategory.TransportSecurity, "Endpoint uses plaintext HTTP",
                "Traffic to this endpoint is not encrypted, so credentials and data can be read or modified on the network.",
                url, "Serve the API over HTTPS and redirect HTTP to HTTPS.");
        else if (isHttps && !headers.ContainsKey("Strict-Transport-Security"))
            Add(ScanSeverity.Low, ScanCategory.SecurityHeaders, "No HSTS header",
                "Without Strict-Transport-Security a client can be downgraded to HTTP on a later request.",
                remediation: "Send Strict-Transport-Security: max-age=31536000; includeSubDomains.");

        // ---- Security headers (only meaningful for HTML/browser responses) ----
        var contentType = response.ContentType ?? "";
        var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
        if (!headers.TryGetValue("X-Content-Type-Options", out var noSniff) || !noSniff.Contains("nosniff", StringComparison.OrdinalIgnoreCase))
            Add(ScanSeverity.Low, ScanCategory.SecurityHeaders, "Missing X-Content-Type-Options: nosniff",
                "The response does not stop browsers from MIME-sniffing the body to a different content type.",
                remediation: "Add X-Content-Type-Options: nosniff.");
        if (isHtml && !headers.ContainsKey("Content-Security-Policy"))
            Add(ScanSeverity.Low, ScanCategory.SecurityHeaders, "No Content-Security-Policy on an HTML response",
                "An HTML response without a CSP has no defence-in-depth against injected scripts.",
                remediation: "Add a Content-Security-Policy suited to the page.");
        if (isHtml && !headers.ContainsKey("X-Frame-Options") && !(headers.GetValueOrDefault("Content-Security-Policy") ?? "").Contains("frame-ancestors"))
            Add(ScanSeverity.Low, ScanCategory.SecurityHeaders, "No clickjacking protection on an HTML response",
                "Neither X-Frame-Options nor a CSP frame-ancestors directive is set, so the page can be framed by any site.",
                remediation: "Send X-Frame-Options: DENY or a CSP frame-ancestors directive.");

        // ---- Information disclosure via headers ----
        foreach (var banner in new[] { "Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version", "X-Runtime", "X-Generator" })
            if (headers.TryGetValue(banner, out var value) && VersionRegex().IsMatch(value))
                Add(ScanSeverity.Low, ScanCategory.InformationDisclosure, $"Technology and version disclosed in {banner}",
                    "The response advertises the server software and its version, which helps an attacker target known vulnerabilities.",
                    $"{banner}: {value}", $"Remove or generalise the {banner} header.");

        if (headers.TryGetValue("Set-Cookie", out var cookies))
            foreach (var cookie in cookies.Split(", Path", StringSplitOptions.None))
            {
                var isSession = SessionCookieRegex().IsMatch(cookie);
                if (isSession && !cookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase))
                    Add(ScanSeverity.Medium, ScanCategory.SecurityHeaders, "Session cookie without HttpOnly",
                        "A session cookie without HttpOnly can be read by JavaScript, so an XSS bug can steal the session.",
                        cookie.Split(';')[0], "Set the HttpOnly attribute on session cookies.");
                if (isSession && isHttps && !cookie.Contains("Secure", StringComparison.OrdinalIgnoreCase))
                    Add(ScanSeverity.Medium, ScanCategory.SecurityHeaders, "Session cookie without Secure",
                        "A session cookie without Secure can be sent over plaintext HTTP and captured on the network.",
                        cookie.Split(';')[0], "Set the Secure attribute on session cookies.");
                if (isSession && !cookie.Contains("SameSite", StringComparison.OrdinalIgnoreCase))
                    Add(ScanSeverity.Low, ScanCategory.SecurityHeaders, "Session cookie without SameSite",
                        "Without a SameSite attribute the cookie is sent on cross-site requests, widening CSRF exposure.",
                        cookie.Split(';')[0], "Set SameSite=Lax or Strict on session cookies.");
            }

        // ---- Permissive CORS ----
        if (headers.TryGetValue("Access-Control-Allow-Origin", out var acao) && acao.Trim() == "*"
            && headers.TryGetValue("Access-Control-Allow-Credentials", out var acac) && acac.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            Add(ScanSeverity.High, ScanCategory.Cors, "CORS allows any origin with credentials",
                "Access-Control-Allow-Origin: * together with Allow-Credentials: true lets any website read authenticated responses.",
                "Access-Control-Allow-Origin: * / Access-Control-Allow-Credentials: true",
                "Reflect only trusted origins, or do not allow credentials with a wildcard origin.");

        // ---- Sensitive data in the body ----
        findings.AddRange(BodyDisclosures(request, response));
        return findings;
    }

    /// <summary>Looks for stack traces, server errors and secret-looking values in the response body.</summary>
    private static IEnumerable<ScanFinding> BodyDisclosures(ApiRequest request, ApiResponse response)
    {
        var body = response.Body;
        if (body.Length == 0)
            yield break;
        var name = request.Name;
        var sample = body.Length > 4000 ? body[..4000] : body;

        if (StackTraceRegex().Match(sample) is { Success: true } trace)
            yield return new ScanFinding(ScanSeverity.Medium, ScanCategory.InformationDisclosure, "Stack trace in response body",
                "The response contains a stack trace or internal exception detail, which leaks implementation internals and file paths.",
                Snippet(trace.Value), "Return a generic error and log the detail server-side.", name);

        foreach (var (kind, evidence) in SecretMatches(sample))
            yield return new ScanFinding(ScanSeverity.High, ScanCategory.InformationDisclosure, $"Possible {kind} in response body",
                $"The body contains a value that looks like a {kind}. Returning secrets to clients risks exposing credentials.",
                Snippet(evidence), "Remove secrets from API responses.", name);

        if (response.StatusCode >= 500)
            yield return new ScanFinding(ScanSeverity.Info, ScanCategory.ErrorHandling, "Server error on a normal request",
                $"The endpoint returned {response.StatusCode} for a baseline request, which may indicate an unhandled condition.",
                $"HTTP {response.StatusCode}", "Handle the error and return a documented status.", name);
    }

    private static string Snippet(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length > 160 ? single[..157] + "…" : single;
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)*")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(sess|sid|token|auth|jsessionid|phpsessid|asp\.net)", RegexOptions.IgnoreCase)]
    private static partial Regex SessionCookieRegex();

    [GeneratedRegex(@"(?:at [\w.$<>]+\([^)]*\)|Traceback \(most recent call last\)|System\.\w+Exception|java\.[\w.]+Exception|\bin [\/\\][\w\/\\.]+:line \d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StackTraceRegex();

    // Credentials that can be recognised by shape alone, regardless of any surrounding label.
    private static readonly (string Kind, Regex Pattern)[] SecretPatterns =
    [
        ("AWS access key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        ("private key", new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant)),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.CultureInvariant)),
        ("Slack token", new Regex(@"\bxox[baprs]-[0-9A-Za-z\-]{10,}", RegexOptions.CultureInvariant)),
        ("JSON Web Token", new Regex(@"\beyJ[\w-]{6,}\.eyJ[\w-]{6,}\.[\w-]{6,}", RegexOptions.CultureInvariant))
    ];

    private static IEnumerable<(string Kind, string Evidence)> SecretMatches(string text)
    {
        foreach (var (kind, pattern) in SecretPatterns)
            if (pattern.Match(text) is { Success: true } m)
                yield return (kind, m.Value);
    }
}
