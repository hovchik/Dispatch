using Dispatch.Domain;

namespace Dispatch.Application.Requests;

/// <summary>
/// Lossless, string-based helpers that keep the URL bar and the Params table in sync.
/// Values are kept exactly as typed (no decoding) so <c>{{variables}}</c> and pre-encoded
/// values survive a round trip; encoding happens once, when the request is sent.
/// </summary>
public static class QueryString
{
    public static (string BaseUrl, string Query, string Fragment) Split(string? url)
    {
        url ??= string.Empty;

        var fragment = string.Empty;
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            fragment = url[hash..];
            url = url[..hash];
        }

        var q = url.IndexOf('?');
        return q < 0 ? (url, string.Empty, fragment) : (url[..q], url[(q + 1)..], fragment);
    }

    public static List<KeyValueItem> Parse(string? url)
    {
        var (_, query, _) = Split(url);
        if (query.Length == 0)
            return [];

        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                return eq < 0 ? new KeyValueItem(pair, string.Empty) : new KeyValueItem(pair[..eq], pair[(eq + 1)..]);
            })
            .ToList();
    }

    /// <summary>
    /// Parses an <c>application/x-www-form-urlencoded</c> body into decoded fields (<c>+</c> → space, <c>%XX</c> unescaped),
    /// since the sender re-encodes form fields; keeping the escapes would double-encode them.
    /// </summary>
    public static List<KeyValueItem> ParseForm(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
        return body.Trim()
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                return eq < 0 ? new KeyValueItem(Decode(pair), string.Empty) : new KeyValueItem(Decode(pair[..eq]), Decode(pair[(eq + 1)..]));
            })
            .ToList();
    }

    /// <summary>Rebuilds <paramref name="url"/> with the query replaced by the enabled parameters.</summary>
    public static string WithParams(string? url, IEnumerable<KeyValueItem> parameters)
    {
        var (baseUrl, _, fragment) = Split(url);
        var query = string.Join('&', parameters
            .Where(p => p.Enabled && (p.Key.Length > 0 || p.Value.Length > 0))
            .Select(p => p.Value.Length == 0 ? p.Key : $"{p.Key}={p.Value}"));

        return query.Length == 0 ? baseUrl + fragment : $"{baseUrl}?{query}{fragment}";
    }
}
