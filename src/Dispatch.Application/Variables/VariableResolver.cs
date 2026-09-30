using System.Globalization;
using System.Security.Cryptography;
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

    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    public static IReadOnlyList<string> DynamicVariables { get; } =
    [
        "$guid", "$uuid", "$timestamp", "$timestampMs", "$isoTimestamp", "$randomInt", "$randomFloat",
        "$randomBoolean", "$randomString", "$randomEmail", "$randomFirstName", "$randomLastName", "$randomColor",
        "$randomIp", "$randomPhone", "$date"
    ];

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

    private static readonly string[] FirstNames = ["Ada", "Alan", "Grace", "Linus", "Margaret", "Ken", "Barbara", "Dennis"];
    private static readonly string[] LastNames = ["Lovelace", "Turing", "Hopper", "Torvalds", "Hamilton", "Thompson", "Liskov", "Ritchie"];
    private static readonly string[] Colors = ["red", "green", "blue", "orange", "purple", "teal", "black", "white"];

    private static string? TryGenerate(string name)
    {
        var r = Random.Shared;
        return name switch
        {
            "$guid" or "$uuid" or "$randomUUID" => Guid.NewGuid().ToString(),
            "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "$timestampMs" => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "$isoTimestamp" => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            "$date" => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "$randomInt" => r.Next(0, 1001).ToString(CultureInfo.InvariantCulture),
            "$randomFloat" => (r.NextDouble() * 1000).ToString("0.00", CultureInfo.InvariantCulture),
            "$randomBoolean" => r.Next(2) == 0 ? "false" : "true",
            "$randomString" => RandomString(12),
            "$randomEmail" => $"{RandomString(8).ToLowerInvariant()}@example.com",
            "$randomFirstName" => FirstNames[r.Next(FirstNames.Length)],
            "$randomLastName" => LastNames[r.Next(LastNames.Length)],
            "$randomColor" => Colors[r.Next(Colors.Length)],
            "$randomIp" => $"{r.Next(1, 255)}.{r.Next(0, 256)}.{r.Next(0, 256)}.{r.Next(1, 255)}",
            "$randomPhone" => $"+1-{r.Next(200, 999)}-{r.Next(200, 999)}-{r.Next(1000, 9999)}",
            _ => null
        };
    }

    private static string RandomString(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }
}
