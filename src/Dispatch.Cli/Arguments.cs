namespace Dispatch.Cli;

public sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal POSIX-style argument parsing: positionals, <c>--name value</c>, <c>--name=value</c>, <c>-n value</c>, flags.</summary>
public sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    /// <param name="flagNames">Options that take no value.</param>
    public static Arguments Parse(IEnumerable<string> args, IReadOnlySet<string> flagNames, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var result = new Arguments();
        using var e = args.GetEnumerator();
        var onlyPositionals = false;
        while (e.MoveNext())
        {
            var arg = e.Current;
            if (onlyPositionals || !arg.StartsWith('-') || arg == "-")
            {
                result.Positionals.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                onlyPositionals = true;
                continue;
            }

            string name;
            string? value = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = arg[2..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                name = arg.TrimStart('-');
            }
            if (aliases is not null && aliases.TryGetValue(name, out var canonical))
                name = canonical;

            if (flagNames.Contains(name))
            {
                if (value is not null)
                    throw new UsageException($"--{name} does not take a value.");
                result._flags.Add(name);
                continue;
            }
            if (value is null)
            {
                if (!e.MoveNext())
                    throw new UsageException($"--{name} needs a value.");
                value = e.Current;
            }
            if (!result._options.TryGetValue(name, out var list))
                result._options[name] = list = [];
            list.Add(value);
        }
        return result;
    }

    public bool Flag(string name) => _flags.Contains(name);

    public string? Option(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Options(string name) =>
        _options.TryGetValue(name, out var values)
            ? values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
            : [];

    public IReadOnlyList<string> RawOptions(string name) => _options.TryGetValue(name, out var values) ? values : [];

    public int Int(string name, int fallback)
    {
        var text = Option(name);
        if (text is null)
            return fallback;
        return int.TryParse(text, out var value) && value >= 0 ? value : throw new UsageException($"--{name} must be a non-negative number.");
    }

    /// <summary>Durations like 30s, 2m, 500ms, or plain seconds.</summary>
    public TimeSpan Duration(string name, TimeSpan fallback)
    {
        var text = Option(name)?.Trim().ToLowerInvariant();
        if (text is null)
            return fallback;
        double Number(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new UsageException($"--{name}: '{text}' is not a duration (e.g. 30s, 2m, 500ms).");
        return text.EndsWith("ms") ? TimeSpan.FromMilliseconds(Number(text[..^2]))
            : text.EndsWith('s') ? TimeSpan.FromSeconds(Number(text[..^1]))
            : text.EndsWith('m') ? TimeSpan.FromMinutes(Number(text[..^1]))
            : text.EndsWith('h') ? TimeSpan.FromHours(Number(text[..^1]))
            : TimeSpan.FromSeconds(Number(text));
    }
}
