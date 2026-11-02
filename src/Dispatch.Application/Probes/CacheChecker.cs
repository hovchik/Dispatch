using System.Globalization;
using Dispatch.Application.Reporting;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Probes;

public sealed class CacheOptions : ProbeOptions
{
    /// <summary>Also send HEAD and compare its headers with GET.</summary>
    public bool CheckHead { get; init; } = true;
}

/// <summary>A parsed Cache-Control header.</summary>
public sealed record CacheControl(IReadOnlyDictionary<string, string?> Directives)
{
    public static CacheControl Parse(string? header)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (header ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
                map[part] = null;
            else
                map[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
        }
        return new CacheControl(map);
    }

    public bool Has(string directive) => Directives.ContainsKey(directive);

    public int? Seconds(string directive) => Directives.TryGetValue(directive, out var v)
                                              && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;

    /// <summary>A shared cache (CDN, proxy) may store the response.</summary>
    public bool SharedCacheable => !Has("no-store") && !Has("private") && (Has("public") || Seconds("s-maxage") > 0 || Seconds("max-age") > 0);

    /// <summary>Any cache may store and reuse it without revalidating for a while.</summary>
    public bool Fresh => !Has("no-store") && !Has("no-cache") && (Seconds("max-age") > 0 || Seconds("s-maxage") > 0);
}

/// <summary>
/// Checks an endpoint's HTTP caching: Cache-Control / Expires / Vary, ETag and Last-Modified validators, whether
/// conditional requests really answer 304, whether validators are stable, HEAD parity, CDN cache status, and caching
/// mistakes such as personalised responses that shared caches may store.
/// </summary>
public sealed class CacheChecker(IRequestSender sender)
{
    private static readonly string[] CdnHeaders = ["X-Cache", "CF-Cache-Status", "X-Cache-Status", "X-Proxy-Cache", "Akamai-Cache-Status", "X-Served-By-Cache",
        "Fastly-Debug-Digest", "X-Vercel-Cache", "X-Nginx-Cache"];

    public async Task<ProbeReport> CheckAsync(ApiRequest request, CacheOptions options, IProgress<ProbeStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ProbeReport
        {
            Kind = ProbeKind.Caching, RequestName = request.Name, Method = request.Method.ToString().ToUpperInvariant(), Url = request.Url
        };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.MaxDuration);
        var session = new ProbeSession(sender, report, options, progress, budget.Token);
        try
        {
            await CheckCoreAsync(request, options, report, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Stopped = true;
        }
        session.Finish();
        return report;
    }

    private static string? Cdn(ApiResponse response)
    {
        foreach (var name in CdnHeaders)
            if (ProbeHelpers.Header(response, name) is { } value)
                return $"{name}: {value}";
        return null;
    }

    /// <summary>Strong or weak entity tag syntax from RFC 9110: optional W/, then a quoted string.</summary>
    public static bool IsValidETag(string etag)
    {
        var t = etag.Trim();
        if (t.StartsWith("W/", StringComparison.Ordinal))
            t = t[2..];
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' && !t[1..^1].Contains('"');
    }

    private static void Add(ProbeReport report, InsightLevel level, string text) => report.Insights.Add(new ReportInsight(level, text));

    private static async Task CheckCoreAsync(ApiRequest request, CacheOptions options, ProbeReport report, ProbeSession session)
    {
        if (request.Method is not (HttpVerb.Get or HttpVerb.Head))
        {
            // Never resend an unsafe method several times; conditional requests mean something else (412) for it anyway.
            report.Error = $"Caching checks need a GET or HEAD request; {request.Method.ToString().ToUpperInvariant()} is not resent.";
            return;
        }
        // Conditional headers of our own replace any the request already carries.
        var plain = ProbeHelpers.WithHeader(ProbeHelpers.WithHeader(request, "If-None-Match", null), "If-Modified-Since", null);

        var firstSent = await session.SendAsync(plain, "GET").ConfigureAwait(false);
        if (firstSent is not { } first)
            return;
        var r1 = first.Response;
        if (!r1.HasResponse)
        {
            report.Error = $"The request got no response ({r1.Error}).";
            return;
        }
        if (r1.StatusCode is < 200 or >= 300)
        {
            report.Error = $"The request answered HTTP {r1.StatusCode}; caching is checked on a successful response.";
            return;
        }

        var cacheControlHeader = ProbeHelpers.Header(r1, "Cache-Control");
        var cc = CacheControl.Parse(cacheControlHeader);
        var etag = ProbeHelpers.Header(r1, "ETag");
        var lastModified = ProbeHelpers.Header(r1, "Last-Modified");
        var expires = ProbeHelpers.Header(r1, "Expires");
        var vary = ProbeHelpers.Header(r1, "Vary");
        var varyItems = (vary ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (authorization, otherCredentials) = ProbeHelpers.SentCredentials(request, r1);
        var personalised = authorization || otherCredentials;
        // RFC 9111 §3.5: a shared cache stores a response to a request with Authorization only when public, s-maxage or
        // must-revalidate allows it. Cookies and API keys get no such protection, so max-age alone is enough there.
        var sharedCacheable = authorization
            ? !cc.Has("no-store") && !cc.Has("private") && (cc.Has("public") || cc.Seconds("s-maxage") > 0 || cc.Has("must-revalidate"))
            : cc.SharedCacheable;
        var setsCookie = ProbeHelpers.Header(r1, "Set-Cookie") is not null;
        var bodyBytes = r1.SizeBytes > 0 ? r1.SizeBytes : r1.Body.Length;

        // ---- Freshness headers ----
        if (cacheControlHeader is null && expires is null)
            Add(report, InsightLevel.Warning,
                "No Cache-Control or Expires header: browsers and proxies apply heuristic caching (often 10% of the time since Last-Modified). " +
                "Say explicitly what you want, e.g. Cache-Control: no-store, no-cache or max-age=60.");
        else if (cacheControlHeader is null && expires is not null)
            Add(report, InsightLevel.Info, "Only Expires is set; Cache-Control: max-age is clearer and takes precedence in modern caches.");
        if (cc.Has("no-store"))
            Add(report, InsightLevel.Info, "Cache-Control: no-store: nothing caches this response. Fine for sensitive data; for public data it costs every client a full download.");
        else if (cc.Has("no-cache"))
            Add(report, InsightLevel.Info, "Cache-Control: no-cache: caches must revalidate every time, so validators (ETag / Last-Modified) matter.");
        if (cc.Has("private") && (cc.Has("public") || cc.Seconds("s-maxage") is not null))
            Add(report, InsightLevel.Warning, $"Cache-Control has contradictory directives ({cacheControlHeader}): private wins, so public / s-maxage are ignored.");
        if (cc.Has("immutable") && cc.Seconds("max-age") is not > 86400)
            Add(report, InsightLevel.Info, "immutable is set without a long max-age; it only helps on long-lived, versioned URLs.");
        if (ProbeHelpers.Header(r1, "Pragma") is { } pragma && cacheControlHeader is null)
            Add(report, InsightLevel.Info, $"Pragma: {pragma} is an HTTP/1.0 request header; it doesn't control caching of responses. Use Cache-Control.");
        if (expires is not null && cc.Seconds("max-age") is > 0 && DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exp)
            && exp < DateTimeOffset.UtcNow)
            Add(report, InsightLevel.Info, "Expires is in the past while max-age allows caching; max-age wins, but the headers disagree.");

        // ---- Personalised content in shared caches ----
        if (personalised && sharedCacheable)
            Add(report, InsightLevel.Bad,
                $"The request carries {(authorization ? "an Authorization header" : "cookies or an API key")}, but the response is cacheable by shared caches " +
                $"({cacheControlHeader}). A CDN or proxy may serve one user's data to another. Use Cache-Control: private (or no-store).");
        if (setsCookie && cc.SharedCacheable)
            Add(report, InsightLevel.Bad, "The response sets a cookie and is cacheable by shared caches: a cache can hand the same Set-Cookie (session) to every visitor.");
        if (personalised && !cc.Has("private") && !cc.Has("no-store") && cacheControlHeader is not null && !sharedCacheable)
            Add(report, InsightLevel.Good, "Credentials are sent and the response isn't cacheable by shared caches.");

        // ---- Vary ----
        if (varyItems.Contains("*"))
            Add(report, InsightLevel.Warning, "Vary: * makes the response effectively uncacheable.");
        if (ProbeHelpers.Header(r1, "Content-Encoding") is { } encoding && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)
            && !varyItems.Contains("Accept-Encoding", StringComparer.OrdinalIgnoreCase) && !varyItems.Contains("*"))
            Add(report, InsightLevel.Warning,
                $"The body is {encoding}-encoded but Vary doesn't list Accept-Encoding: a cache may serve compressed bytes to a client that can't decode them.");
        if (ProbeHelpers.Header(r1, "Access-Control-Allow-Origin") is { } acao && acao != "*"
            && !varyItems.Contains("Origin", StringComparer.OrdinalIgnoreCase) && !varyItems.Contains("*") && !cc.Has("no-store"))
            Add(report, InsightLevel.Bad,
                $"Access-Control-Allow-Origin is a specific origin ({acao}) but Vary doesn't list Origin: a cache can serve this CORS header to other origins, " +
                "breaking (or opening) cross-origin access.");

        // ---- Validators ----
        if (etag is not null && !IsValidETag(etag))
            Add(report, InsightLevel.Warning, $"ETag {etag} is not a quoted string (RFC 9110 needs \"…\" or W/\"…\"); some caches will ignore it.");
        if (lastModified is not null && DateTimeOffset.TryParse(lastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lm)
            && lm > DateTimeOffset.UtcNow.AddMinutes(1))
            Add(report, InsightLevel.Warning, $"Last-Modified ({lastModified}) is in the future.");
        if (etag is null && lastModified is null && !cc.Has("no-store"))
            Add(report, InsightLevel.Warning,
                "No ETag or Last-Modified: clients can't revalidate cheaply and always download the full body" +
                (bodyBytes > 0 ? $" ({ReportBytes(bodyBytes)})." : "."));

        // ---- Stability: same request again ----
        var secondSent = await session.SendAsync(plain, "GET again").ConfigureAwait(false);
        string stability = "—";
        if (secondSent is { } second && second.Response.StatusCode is >= 200 and < 300)
        {
            var r2 = second.Response;
            var sameBody = ProbeHelpers.SameBody(r1, r2);
            var etag2 = ProbeHelpers.Header(r2, "ETag");
            if (etag is not null && etag2 is not null)
            {
                if (sameBody && etag2 != etag)
                {
                    stability = "ETag changes";
                    Add(report, InsightLevel.Bad,
                        $"The body didn't change but the ETag did ({etag} then {etag2}): every revalidation misses, so the ETag saves nothing. " +
                        "Typical causes: a per-node or timestamp-based ETag behind a load balancer.");
                }
                else if (!sameBody && etag2 == etag && !etag.TrimStart().StartsWith("W/", StringComparison.Ordinal))
                {
                    stability = "stale ETag";
                    Add(report, InsightLevel.Bad,
                        $"The body changed but the strong ETag stayed {etag}: caches will keep serving the old body after revalidating.");
                }
                else if (!sameBody && etag2 == etag)
                {
                    // A weak ETag only promises semantic equivalence, so different bytes don't disprove it.
                    stability = "weak, bytes differ";
                    Add(report, InsightLevel.Info,
                        $"The body bytes changed while the weak ETag {etag} stayed the same. That's allowed for equivalent content; check it if the data itself changed.");
                }
                else
                    stability = sameBody ? "stable" : "body changed";
            }
            else
                stability = sameBody ? "same body" : "body changed";
            var cdn1 = Cdn(r1);
            var cdn2 = Cdn(r2);
            if (cdn1 is not null || cdn2 is not null)
            {
                report.Stats.Add(new ProbeStat("CDN / cache", cdn2 ?? cdn1!, cdn1 is not null && cdn2 is not null && cdn1 != cdn2 ? $"first {cdn1}" : null));
                if (ProbeHelpers.Header(r2, "Age") is { } age)
                    Add(report, InsightLevel.Info, $"The second response came from a cache (Age: {age} s; {cdn2 ?? cdn1}).");
            }
        }

        // ---- Conditional requests ----
        string inm = "—", ims = "—";
        if (etag is not null)
        {
            var conditional = await session.SendAsync(ProbeHelpers.WithHeader(plain, "If-None-Match", etag), "If-None-Match").ConfigureAwait(false);
            if (conditional is { } c)
            {
                var r = c.Response;
                if (r.StatusCode == 304)
                {
                    inm = "304";
                    Add(report, InsightLevel.Good, $"If-None-Match with the ETag answers 304 Not Modified, saving {ReportBytes(bodyBytes)} per revalidation.");
                    if (r.Body.Length > 0)
                        Add(report, InsightLevel.Bad, "The 304 response has a body; 304 must not carry one.");
                    if (ProbeHelpers.Header(r, "ETag") is null)
                        Add(report, InsightLevel.Warning, "The 304 response doesn't repeat the ETag; caches use it to update the stored response.");
                    if (cacheControlHeader is not null && ProbeHelpers.Header(r, "Cache-Control") is null)
                        Add(report, InsightLevel.Warning, "The 304 response omits Cache-Control, so caches can't refresh the freshness lifetime.");
                }
                else if (r.StatusCode is >= 200 and < 300 && (ProbeHelpers.Header(r, "ETag") is { } newTag && newTag != etag || !ProbeHelpers.SameBody(r, r1)))
                {
                    // A changed representation must be returned in full: that's correct, but it means the test is inconclusive.
                    inm = $"{r.StatusCode} (changed)";
                    Add(report, InsightLevel.Info,
                        "If-None-Match answered with a full response because the content (or its ETag) changed during the probe; run it again on stable data to test 304.");
                }
                else if (r.StatusCode is >= 200 and < 300)
                {
                    inm = r.StatusCode.ToString(CultureInfo.InvariantCulture);
                    Add(report, InsightLevel.Bad,
                        $"If-None-Match with the current ETag still answered {r.StatusCode} with the full body: the ETag is sent but conditional requests " +
                        "are not supported, so revalidation saves nothing.");
                }
                else
                {
                    inm = c.Step.StatusText;
                    Add(report, InsightLevel.Bad, $"If-None-Match with the current ETag answered {c.Step.StatusText}.");
                }
            }
            var mismatch = await session.SendAsync(ProbeHelpers.WithHeader(plain, "If-None-Match", "\"dispatch-no-such-etag\""), "If-None-Match (wrong tag)")
                .ConfigureAwait(false);
            if (mismatch is { Response.StatusCode: 304 })
                Add(report, InsightLevel.Bad, "If-None-Match with an unknown ETag also answered 304: the server returns 304 for any tag, so clients can be stuck with stale data.");
        }
        if (lastModified is not null)
        {
            var conditional = await session.SendAsync(ProbeHelpers.WithHeader(plain, "If-Modified-Since", lastModified), "If-Modified-Since").ConfigureAwait(false);
            if (conditional is { } c)
            {
                ims = c.Step.StatusText;
                if (c.Response.StatusCode == 304)
                    Add(report, InsightLevel.Good, "If-Modified-Since with Last-Modified answers 304 Not Modified.");
                else if (c.Response.StatusCode is >= 200 and < 300
                         && (ProbeHelpers.Header(c.Response, "Last-Modified") is { } newLm && newLm != lastModified || !ProbeHelpers.SameBody(c.Response, r1)))
                {
                    ims = $"{c.Response.StatusCode} (changed)";
                    Add(report, InsightLevel.Info, "If-Modified-Since answered with a full response because the content changed during the probe; the test is inconclusive.");
                }
                else if (etag is null && c.Response.StatusCode is >= 200 and < 300)
                    Add(report, InsightLevel.Warning,
                        $"If-Modified-Since with the Last-Modified date still answered {c.Response.StatusCode} with the full body: Last-Modified is not usable for revalidation.");
                else if (c.Response.StatusCode is >= 200 and < 300)
                    Add(report, InsightLevel.Info, "If-Modified-Since is not honoured (only If-None-Match is). That's fine for clients that use the ETag.");
            }
        }

        // ---- HEAD ----
        string head = "—";
        if (options.CheckHead && request.Method == HttpVerb.Get)
        {
            var headRequest = plain.Clone();
            headRequest.Method = HttpVerb.Head;
            var sent = await session.SendAsync(headRequest, "HEAD").ConfigureAwait(false);
            if (sent is { } h)
            {
                head = h.Step.StatusText;
                if (h.Response.StatusCode is 405 or 501)
                    Add(report, InsightLevel.Info, $"HEAD is not supported (HTTP {h.Response.StatusCode}); clients can't check freshness without downloading.");
                else if (h.Response.StatusCode >= 500 || !h.Response.HasResponse)
                    Add(report, InsightLevel.Bad, $"HEAD failed ({h.Step.StatusText}) while GET works.");
                else if (h.Response.StatusCode is >= 200 and < 300)
                {
                    var headEtag = ProbeHelpers.Header(h.Response, "ETag");
                    if (etag is not null && headEtag is not null && headEtag != etag && stability is "stable" or "same body")
                        Add(report, InsightLevel.Warning, $"HEAD returns a different ETag ({headEtag}) than GET ({etag}).");
                    if (h.Response.Body.Length > 0)
                        Add(report, InsightLevel.Bad, "The HEAD response has a body; HEAD must not return one.");
                }
            }
        }

        // ---- Stats ----
        report.Stats.Add(new ProbeStat("Cache-Control", cacheControlHeader ?? "—", expires is null ? null : $"Expires {expires}", cacheControlHeader is null ? true : null));
        report.Stats.Add(new ProbeStat("ETag", etag ?? "—", stability == "—" ? null : stability, stability is "ETag changes" or "stale ETag" ? true : null));
        report.Stats.Add(new ProbeStat("Last-Modified", lastModified ?? "—"));
        report.Stats.Add(new ProbeStat("If-None-Match", inm, etag is null ? "no ETag to test" : null, inm == "304" ? false : etag is null ? null : true));
        report.Stats.Add(new ProbeStat("If-Modified-Since", ims, lastModified is null ? "no Last-Modified" : null));
        report.Stats.Add(new ProbeStat("Vary", vary ?? "—"));
        // The transport decompresses automatically and drops Content-Encoding, so only a remaining header says anything.
        report.Stats.Add(new ProbeStat("Body", ReportBytes(bodyBytes), ProbeHelpers.Header(r1, "Content-Encoding") is { } enc ? $"{enc}-encoded" : null));
        if (head != "—")
            report.Stats.Add(new ProbeStat("HEAD", head));

        report.Details["cacheControl"] = cacheControlHeader;
        report.Details["expires"] = expires;
        report.Details["etag"] = etag;
        report.Details["lastModified"] = lastModified;
        report.Details["vary"] = vary;
        report.Details["ifNoneMatch"] = inm;
        report.Details["ifModifiedSince"] = ims;
        report.Details["etagStability"] = stability;
        report.Details["personalised"] = personalised;
        report.Details["sharedCacheable"] = sharedCacheable;
        report.Details["bodyBytes"] = bodyBytes;

        var policy = cc.Has("no-store") ? "not cached (no-store)"
            : cc.Has("no-cache") ? "revalidated every time (no-cache)"
            : cc.Seconds("max-age") is { } maxAge ? $"fresh for {maxAge} s{(cc.Has("private") ? ", private" : cc.SharedCacheable ? ", shared caches too" : "")}"
            : cacheControlHeader is null && expires is null ? "no caching policy"
            : "cacheable";
        var validation = inm == "304" || ims == "304" ? "304 revalidation works" : etag is null && lastModified is null ? "no validators" : "revalidation doesn't save the body";
        report.Summary = $"{char.ToUpperInvariant(policy[0])}{policy[1..]}; {validation}";
    }

    private static string ReportBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024:0.0} MB"
    };
}
