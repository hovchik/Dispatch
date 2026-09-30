using Dispatch.Domain;

namespace Dispatch.Application.Variables;

/// <summary>
/// The variables visible to one send, in increasing precedence: globals, collection, environment, data row (runner),
/// runtime (set by extraction rules and scripts during this session / run).
/// </summary>
public sealed class VariableContext
{
    public Dictionary<string, string> Globals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Collection { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Runtime { get; } = new(StringComparer.Ordinal);

    /// <summary>Environment variables changed during this send; the caller persists them.</summary>
    public Dictionary<string, string> EnvironmentUpdates { get; } = new(StringComparer.Ordinal);

    /// <summary>Zero-based iteration of a collection run (0 outside a run).</summary>
    public int Iteration { get; set; }

    /// <summary>Global variables changed during this send; the caller persists them.</summary>
    public Dictionary<string, string> GlobalUpdates { get; } = new(StringComparer.Ordinal);

    public static VariableContext For(ApiEnvironment? environment, IEnumerable<KeyValueItem>? collectionVariables = null,
        IReadOnlyDictionary<string, string>? runtime = null, IReadOnlyDictionary<string, string>? globals = null)
    {
        var context = new VariableContext();
        if (globals is not null)
            foreach (var (k, v) in globals)
                context.Globals[k] = v;
        if (collectionVariables is not null)
            foreach (var v in collectionVariables.Where(v => v.IsActive))
                context.Collection[v.Key.Trim()] = v.Value;
        if (environment is not null)
            foreach (var (k, v) in environment.ToDictionary())
                context.Environment[k] = v;
        if (runtime is not null)
            foreach (var (k, v) in runtime)
                context.Runtime[k] = v;
        return context;
    }

    /// <summary>All variables merged by precedence.</summary>
    public IReadOnlyDictionary<string, string> Merged()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in new[] { Globals, Collection, Environment, Data, Runtime })
            foreach (var (k, v) in layer)
                map[k] = v;
        return map;
    }

    public string? Get(string name) =>
        Runtime.TryGetValue(name, out var v) || Data.TryGetValue(name, out v) || Environment.TryGetValue(name, out v)
        || Collection.TryGetValue(name, out v) || Globals.TryGetValue(name, out v)
            ? v
            : null;

    public string Resolve(string? text) => VariableResolver.Resolve(text, Merged());

    public void Set(string name, string value, VariableScope scope)
    {
        if (scope == VariableScope.Environment)
        {
            Environment[name] = value;
            EnvironmentUpdates[name] = value;
            // A runtime value would shadow the environment one; drop it so the new value is visible.
            Runtime.Remove(name);
        }
        else
        {
            Runtime[name] = value;
        }
    }

    public void SetGlobal(string name, string value)
    {
        Globals[name] = value;
        GlobalUpdates[name] = value;
    }

    public void Unset(string name)
    {
        Runtime.Remove(name);
        if (Environment.Remove(name))
            EnvironmentUpdates.Remove(name);
    }
}
