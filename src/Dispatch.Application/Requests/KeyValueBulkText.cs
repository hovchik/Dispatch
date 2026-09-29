using Dispatch.Domain;

namespace Dispatch.Application.Requests;

/// <summary>
/// Postman-style bulk edit text: one <c>key: value</c> pair per line, disabled rows prefixed with <c>//</c>.
/// </summary>
public static class KeyValueBulkText
{
    private const string DisabledPrefix = "//";

    public static string Format(IEnumerable<KeyValueItem> items) => string.Join(Environment.NewLine, items
        .Where(i => i.Key.Length > 0 || i.Value.Length > 0)
        .Select(i => (i.Enabled ? string.Empty : DisabledPrefix + " ") + i.Key + ": " + i.Value));

    /// <summary>Parses bulk text; blank lines are skipped and a line without <c>:</c> becomes a key with an empty value.</summary>
    public static List<KeyValueItem> Parse(string text)
    {
        var items = new List<KeyValueItem>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var enabled = true;
            if (line.StartsWith(DisabledPrefix, StringComparison.Ordinal))
            {
                enabled = false;
                line = line[DisabledPrefix.Length..].Trim();
            }
            if (line.Length == 0)
                continue;

            var colon = line.IndexOf(':');
            items.Add(colon < 0
                ? new KeyValueItem(line, string.Empty, enabled)
                : new KeyValueItem(line[..colon].Trim(), line[(colon + 1)..].Trim(), enabled));
        }
        return items;
    }
}
