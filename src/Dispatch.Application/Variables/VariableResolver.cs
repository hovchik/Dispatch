using System.Text.RegularExpressions;

namespace Dispatch.Application.Variables;

/// <summary>
/// Replaces <c>{{name}}</c> placeholders with variable values. Names starting with <c>$</c> are dynamic values
/// generated on every use: <c>{{$guid}}</c>, <c>{{$timestamp}}</c>, <c>{{$isoTimestamp}}</c>, <c>{{$randomInt}}</c>, ...
/// </summary>
public static partial class VariableResolver
{
    // Up to this many passes so a variable may reference another ({{baseUrl}} = {{host}}/api),
    // while guaranteeing termination on cycles.
    private const int MaxPasses = 5;

    [GeneratedRegex(@"\{\{\s*(\$[A-Za-z]+\([^{}()]*\)|[^{}\s]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Every dynamic variable, e.g. <c>$randomFullName</c> or <c>$randomInt(min,max)</c>.</summary>
    public static IReadOnlyList<string> DynamicVariables => Faker.Names;

    public static string Resolve(string? input, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("{{", StringComparison.Ordinal))
            return input ?? string.Empty;

        var current = input;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var next = PlaceholderRegex().Replace(current, m =>
            {
                var name = m.Groups[1].Value;
                if (variables.TryGetValue(name, out var value))
                    return value;
                return TryGenerate(name) ?? m.Value;
            });

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

    /// <summary>Every placeholder name used in <paramref name="input"/> (before resolution).</summary>
    public static IEnumerable<string> FindNames(string? input) =>
        string.IsNullOrEmpty(input)
            ? []
            : PlaceholderRegex().Matches(input).Select(m => m.Groups[1].Value);

    private static string? TryGenerate(string name) => Faker.Shared.Generate(name);
}
