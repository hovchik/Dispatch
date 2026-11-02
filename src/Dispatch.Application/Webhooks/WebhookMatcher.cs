using System.Text.RegularExpressions;

namespace Dispatch.Application.Webhooks;

/// <summary>Matches received hooks against the path filter of a webhook check.</summary>
public static partial class WebhookMatcher
{
    /// <summary>
    /// True when the hook matches <paramref name="filter"/>: blank matches everything; <c>/hooks/orders</c> matches that
    /// path (case-insensitive, trailing slash ignored); <c>*</c> stands for any characters (<c>/hooks/*</c>); a leading
    /// method (<c>POST /hooks/orders</c>) also restricts the method; text without a leading slash must be contained in
    /// the path (<c>orders</c>).
    /// </summary>
    public static bool Matches(ReceivedWebhook hook, string filter) => Matches(hook.Method, hook.Path, filter);

    public static bool Matches(string method, string path, string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0)
            return true;

        if (IsMethod(filter))
            return method.Equals(filter, StringComparison.OrdinalIgnoreCase);
        var space = filter.IndexOf(' ');
        if (space > 0 && IsMethod(filter[..space]))
        {
            if (!method.Equals(filter[..space], StringComparison.OrdinalIgnoreCase))
                return false;
            filter = filter[(space + 1)..].Trim();
            if (filter.Length == 0)
                return true;
        }

        var normalisedPath = Normalise(path);
        if (!filter.StartsWith('/'))
            return filter.Contains('*')
                ? Regex.IsMatch(normalisedPath, ".*" + Glob(filter) + ".*", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
                : normalisedPath.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var normalisedFilter = Normalise(filter);
        return normalisedFilter.Contains('*')
            ? Regex.IsMatch(normalisedPath, "^" + Glob(normalisedFilter) + "$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            : normalisedPath.Equals(normalisedFilter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Matches a stream-message label (<c>POST /hooks/orders</c>) the way <see cref="Matches(ReceivedWebhook,string)"/> does.</summary>
    public static bool MatchesLabel(string? label, string filter)
    {
        if (string.IsNullOrEmpty(label))
            return filter.Trim().Length == 0;
        var space = label.IndexOf(' ');
        return space > 0 ? Matches(label[..space], label[(space + 1)..], filter) : Matches("", label, filter);
    }

    private static bool IsMethod(string word) => MethodRegex().IsMatch(word);

    private static string Normalise(string path)
    {
        var p = path.Trim();
        if (p.Length > 1 && p.EndsWith('/'))
            p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    private static string Glob(string pattern) => string.Join(".*", pattern.Split('*').Select(Regex.Escape));

    [GeneratedRegex("^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)$", RegexOptions.IgnoreCase)]
    private static partial Regex MethodRegex();
}
