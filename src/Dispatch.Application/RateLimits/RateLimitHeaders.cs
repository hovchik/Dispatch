using System.Globalization;
using System.Text.RegularExpressions;
using Dispatch.Domain;

namespace Dispatch.Application.RateLimits;

/// <summary>
/// Reads rate-limit headers in the common styles: <c>X-RateLimit-*</c> (GitHub, many gateways), the IETF
/// <c>RateLimit-Limit/-Remaining/-Reset</c> + <c>RateLimit-Policy</c> drafts, the newer structured <c>RateLimit</c>
/// header (<c>limit=, remaining=, reset=</c> or <c>q=, r=, t=</c>), and <c>Retry-After</c>.
/// </summary>
public static partial class RateLimitHeaders
{
    public static AdvertisedLimit? Read(IReadOnlyList<ResponseHeader> headers)
    {
        string? Get(params string[] names) =>
            headers.FirstOrDefault(h => names.Any(n => h.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))?.Value.Trim();

        var policy = Get("RateLimit-Policy", "X-RateLimit-Policy");
        int? policyLimit = policy is null ? null : FirstInt(policy, "q") ?? LeadingInt(policy);

        // Structured "RateLimit: limit=100, remaining=50, reset=30" or "RateLimit: \"default\";r=50;t=30".
        if (Get("RateLimit") is { } structured)
        {
            var limit = FirstInt(structured, "limit") ?? FirstInt(structured, "q") ?? policyLimit;
            var remaining = FirstInt(structured, "remaining") ?? FirstInt(structured, "r");
            var reset = FirstInt(structured, "reset") ?? FirstInt(structured, "t");
            if (limit is not null || remaining is not null || reset is not null)
                return new AdvertisedLimit(limit, remaining, reset, policy, "RateLimit");
        }

        var ietfLimit = Get("RateLimit-Limit");
        var ietfRemaining = Get("RateLimit-Remaining");
        var ietfReset = Get("RateLimit-Reset");
        if (ietfLimit is not null || ietfRemaining is not null || ietfReset is not null)
            return new AdvertisedLimit(LeadingInt(ietfLimit) ?? policyLimit, LeadingInt(ietfRemaining), ResetSeconds(ietfReset), policy,
                "RateLimit-*");

        var xLimit = Get("X-RateLimit-Limit", "X-Rate-Limit-Limit", "X-RateLimit-Limit-Minute");
        var xRemaining = Get("X-RateLimit-Remaining", "X-Rate-Limit-Remaining", "X-RateLimit-Remaining-Minute");
        var xReset = Get("X-RateLimit-Reset", "X-Rate-Limit-Reset", "X-RateLimit-Reset-After");
        if (xLimit is not null || xRemaining is not null || xReset is not null)
            return new AdvertisedLimit(LeadingInt(xLimit) ?? policyLimit, LeadingInt(xRemaining), ResetSeconds(xReset), policy, "X-RateLimit-*");

        return policy is null ? null : new AdvertisedLimit(policyLimit, null, null, policy, "RateLimit-Policy");
    }

    /// <summary>
    /// Every header that says something about limits (X-RateLimit-*, RateLimit-*, Retry-After, quota and throttle headers,
    /// vendor variants), in the order the server sent them. Raw, so reports show exactly what the server advertised.
    /// </summary>
    public static IReadOnlyList<ResponseHeader> Relevant(IReadOnlyList<ResponseHeader> headers) =>
        headers.Where(h => RelevantRegex().IsMatch(h.Name)).ToList();

    /// <summary>Retry-After as seconds from now: either delta-seconds or an HTTP date.</summary>
    public static double? RetryAfterSeconds(IReadOnlyList<ResponseHeader> headers)
    {
        var value = headers.FirstOrDefault(h => h.Name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return Math.Max(0, seconds);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? Math.Max(0, (at - DateTimeOffset.UtcNow).TotalSeconds)
            : null;
    }

    /// <summary>Reset as seconds from now. Values that look like a Unix timestamp are converted.</summary>
    private static double? ResetSeconds(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        return number > 1_000_000_000
            ? Math.Max(0, number - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0)
            : number;
    }

    private static int? LeadingInt(string? value) =>
        value is not null && LeadingIntRegex().Match(value) is { Success: true } m
                          && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? FirstInt(string value, string key) =>
        Regex.Match(value, $@"(?:^|[;,\s]){Regex.Escape(key)}\s*=\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"rate[-_]?limit|retry-after|quota|throttl", RegexOptions.IgnoreCase)]
    private static partial Regex RelevantRegex();

    [GeneratedRegex(@"^\s*\d+")]
    private static partial Regex LeadingIntRegex();
}
