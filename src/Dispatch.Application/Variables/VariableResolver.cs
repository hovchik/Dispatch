using System.Text.RegularExpressions;

namespace Dispatch.Application.Variables;

/// <summary>Replaces <c>{{name}}</c> placeholders with environment values.</summary>
public static partial class VariableResolver
{
    // Up to this many passes so a variable may reference another ({{baseUrl}} = {{host}}/api),
    // while guaranteeing termination on cycles.
    private const int MaxPasses = 5;

    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    public static string Resolve(string? input, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(input) || variables.Count == 0 || !input.Contains("{{", StringComparison.Ordinal))
            return input ?? string.Empty;

        var current = input;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var next = PlaceholderRegex().Replace(current, m =>
                variables.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

            if (next == current)
                break;
            current = next;
        }
        return current;
    }

    /// <summary>Names of placeholders that have no matching variable (after resolution).</summary>
    public static IReadOnlyList<string> FindUnresolved(string? input, IReadOnlyDictionary<string, string> variables)
    {
        var resolved = Resolve(input, variables);
        return PlaceholderRegex().Matches(resolved)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
