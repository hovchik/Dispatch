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

    /// <summary>Environment variables removed during this send (pm.environment.unset); the caller persists the removal.</summary>
    public HashSet<string> EnvironmentRemovals { get; } = new(StringComparer.Ordinal);

    private readonly HashSet<string> _runtimeRemovals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialRuntimeKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Runtime variables removed during this send: explicit <see cref="UnsetRuntime"/> calls plus any key the context
    /// started with that is no longer in <see cref="Runtime"/> (scripts may remove from the dictionary directly).
    /// </summary>
    public IReadOnlySet<string> RuntimeRemovals
    {
        get
        {
            var removed = new HashSet<string>(_runtimeRemovals, StringComparer.Ordinal);
            foreach (var key in _initialRuntimeKeys)
                if (!Runtime.ContainsKey(key))
                    removed.Add(key);
            removed.ExceptWith(Runtime.Keys);
            return removed;
        }
    }

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
            {
                context.Runtime[k] = v;
                context._initialRuntimeKeys.Add(k);
            }
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
            EnvironmentRemovals.Remove(name);
            // A runtime value would shadow the environment one; drop it so the new value is visible.
            Runtime.Remove(name);
        }
        else
        {
            Runtime[name] = value;
            _runtimeRemovals.Remove(name);
        }
    }

    public void SetGlobal(string name, string value)
    {
        Globals[name] = value;
        GlobalUpdates[name] = value;
    }

    /// <summary>pm.environment.unset: removes the variable from the environment (and any runtime shadow) and records the removal.</summary>
    public void Unset(string name)
    {
        UnsetRuntime(name);
        if (Environment.Remove(name))
        {
            EnvironmentUpdates.Remove(name);
            EnvironmentRemovals.Add(name);
        }
    }

    /// <summary>pm.variables.unset: removes a runtime variable and records the removal so the session forgets it too.</summary>
    public void UnsetRuntime(string name)
    {
        if (Runtime.Remove(name))
            _runtimeRemovals.Add(name);
    }
}
