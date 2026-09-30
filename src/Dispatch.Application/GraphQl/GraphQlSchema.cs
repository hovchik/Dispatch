using System.Text;
using System.Text.Json.Nodes;

namespace Dispatch.Application.GraphQl;

public sealed record GraphQlTypeRef(string Kind, string? Name, GraphQlTypeRef? OfType)
{
    /// <summary>The named type under NON_NULL / LIST wrappers.</summary>
    public GraphQlTypeRef Named => OfType is null ? this : OfType.Named;

    public override string ToString() => Kind switch
    {
        "NON_NULL" => $"{OfType}!",
        "LIST" => $"[{OfType}]",
        _ => Name ?? "?"
    };
}

public sealed record GraphQlArgument(string Name, GraphQlTypeRef Type, string? Description, string? DefaultValue);

public sealed record GraphQlField(string Name, GraphQlTypeRef Type, IReadOnlyList<GraphQlArgument> Args, string? Description,
    bool IsDeprecated);

public sealed record GraphQlType(string Kind, string Name, string? Description, IReadOnlyList<GraphQlField> Fields,
    IReadOnlyList<GraphQlArgument> InputFields, IReadOnlyList<string> EnumValues, IReadOnlyList<string> PossibleTypes);

/// <summary>A GraphQL schema read from an introspection result; powers the explorer, autocomplete and query templates.</summary>
public sealed class GraphQlSchema
{
    public const string IntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types {
              kind name description
              fields(includeDeprecated: true) {
                name description isDeprecated
                args { name description defaultValue type { ...TypeRef } }
                type { ...TypeRef }
              }
              inputFields { name description defaultValue type { ...TypeRef } }
              enumValues(includeDeprecated: true) { name }
              possibleTypes { name }
            }
          }
        }
        fragment TypeRef on __Type {
          kind name
          ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } }
        }
        """;

    public string? QueryType { get; init; }
    public string? MutationType { get; init; }
    public string? SubscriptionType { get; init; }
    public IReadOnlyDictionary<string, GraphQlType> Types { get; init; } = new Dictionary<string, GraphQlType>();

    public GraphQlType? Query => QueryType is null ? null : Types.GetValueOrDefault(QueryType);
    public GraphQlType? Mutation => MutationType is null ? null : Types.GetValueOrDefault(MutationType);
    public GraphQlType? Subscription => SubscriptionType is null ? null : Types.GetValueOrDefault(SubscriptionType);

    /// <summary>Parses the JSON returned by <see cref="IntrospectionQuery"/> (with or without the <c>data</c> wrapper).</summary>
    public static GraphQlSchema Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("Empty introspection result.");
        if (root["errors"] is JsonArray { Count: > 0 } errors && root["data"] is null)
            throw new FormatException("Introspection failed: " + errors[0]?["message"]);
        var schema = (root["data"]?["__schema"] ?? root["__schema"])
                     ?? throw new FormatException("The response has no __schema (is introspection disabled?).");

        var types = new Dictionary<string, GraphQlType>(StringComparer.Ordinal);
        foreach (var t in schema["types"]?.AsArray() ?? [])
        {
            if (t?["name"]?.GetValue<string>() is not { } name)
                continue;
            types[name] = new GraphQlType(
                t["kind"]?.GetValue<string>() ?? "OBJECT",
                name,
                t["description"]?.GetValue<string>(),
                (t["fields"] as JsonArray ?? []).Select(ParseField).ToList(),
                (t["inputFields"] as JsonArray ?? []).Select(ParseArgument).ToList(),
                (t["enumValues"] as JsonArray ?? []).Select(e => e?["name"]?.GetValue<string>() ?? "").ToList(),
                (t["possibleTypes"] as JsonArray ?? []).Select(e => e?["name"]?.GetValue<string>() ?? "").ToList());
        }

        return new GraphQlSchema
        {
            QueryType = schema["queryType"]?["name"]?.GetValue<string>(),
            MutationType = schema["mutationType"]?["name"]?.GetValue<string>(),
            SubscriptionType = schema["subscriptionType"]?["name"]?.GetValue<string>(),
            Types = types
        };
    }

    private static GraphQlField ParseField(JsonNode? f) => new(
        f?["name"]?.GetValue<string>() ?? "",
        ParseTypeRef(f?["type"]),
        (f?["args"] as JsonArray ?? []).Select(ParseArgument).ToList(),
        f?["description"]?.GetValue<string>(),
        f?["isDeprecated"]?.GetValue<bool>() ?? false);

    private static GraphQlArgument ParseArgument(JsonNode? a) => new(
        a?["name"]?.GetValue<string>() ?? "",
        ParseTypeRef(a?["type"]),
        a?["description"]?.GetValue<string>(),
        a?["defaultValue"]?.GetValue<string>());

    private static GraphQlTypeRef ParseTypeRef(JsonNode? t) => t is null
        ? new GraphQlTypeRef("SCALAR", "Unknown", null)
        : new GraphQlTypeRef(t["kind"]?.GetValue<string>() ?? "SCALAR", t["name"]?.GetValue<string>(),
            t["ofType"] is { } of ? ParseTypeRef(of) : null);

    /// <summary>
    /// A ready-to-edit operation for a root field: variables for its arguments and a selection set of scalar
    /// fields (nested objects up to <paramref name="depth"/> levels).
    /// </summary>
    public (string Query, string Variables) BuildOperation(string operation, GraphQlField field, int depth = 2)
    {
        var sb = new StringBuilder();
        var vars = new JsonObject();
        var name = char.ToUpperInvariant(field.Name[0]) + field.Name[1..];
        sb.Append(operation).Append(' ').Append(name);
        if (field.Args.Count > 0)
            sb.Append('(').Append(string.Join(", ", field.Args.Select(a => $"${a.Name}: {a.Type}"))).Append(')');
        sb.Append(" {\n  ").Append(field.Name);
        if (field.Args.Count > 0)
            sb.Append('(').Append(string.Join(", ", field.Args.Select(a => $"{a.Name}: ${a.Name}"))).Append(')');

        foreach (var arg in field.Args)
            vars[arg.Name] = SampleValue(arg.Type, 0);

        AppendSelection(sb, field.Type.Named.Name, depth, 2, []);
        sb.Append("\n}\n");
        return (sb.ToString(), vars.Count == 0 ? "{}" : vars.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private void AppendSelection(StringBuilder sb, string? typeName, int depth, int indent, HashSet<string> visiting)
    {
        if (typeName is null || !Types.TryGetValue(typeName, out var type) || type.Kind is "SCALAR" or "ENUM")
            return;

        var pad = new string(' ', indent);
        var scalars = type.Fields.Where(f => IsLeaf(f.Type) && f.Args.All(a => a.Type.Kind != "NON_NULL")).ToList();
        var objects = depth > 1 && visiting.Add(typeName)
            ? type.Fields.Where(f => !IsLeaf(f.Type) && f.Args.All(a => a.Type.Kind != "NON_NULL")).Take(5).ToList()
            : [];

        if (type.Kind is "UNION" or "INTERFACE" && scalars.Count == 0)
        {
            sb.Append(" {\n").Append(pad).Append("  __typename\n").Append(pad).Append('}');
            return;
        }

        sb.Append(" {");
        foreach (var f in scalars.Count > 0 ? scalars : [new GraphQlField("__typename", new("SCALAR", "String", null), [], null, false)])
            sb.Append('\n').Append(pad).Append("  ").Append(f.Name);
        foreach (var f in objects)
        {
            sb.Append('\n').Append(pad).Append("  ").Append(f.Name);
            AppendSelection(sb, f.Type.Named.Name, depth - 1, indent + 2, visiting);
        }
        visiting.Remove(typeName);
        sb.Append('\n').Append(pad).Append('}');
    }

    private bool IsLeaf(GraphQlTypeRef type) =>
        type.Named.Name is { } n && Types.TryGetValue(n, out var t) ? t.Kind is "SCALAR" or "ENUM" : true;

    private JsonNode? SampleValue(GraphQlTypeRef type, int depth)
    {
        if (type.Kind == "NON_NULL")
            return SampleValue(type.OfType!, depth);
        if (type.Kind == "LIST")
            return new JsonArray(SampleValue(type.OfType!, depth));
        var name = type.Name ?? "";
        switch (name)
        {
            case "Int": return 0;
            case "Float": return 0.0;
            case "Boolean": return false;
            case "ID": return "id";
            case "String": return "";
        }
        if (!Types.TryGetValue(name, out var t))
            return "";
        if (t.Kind == "ENUM")
            return t.EnumValues.FirstOrDefault() ?? "";
        if (t.Kind == "INPUT_OBJECT" && depth < 3)
        {
            var obj = new JsonObject();
            foreach (var f in t.InputFields.Where(f => f.Type.Kind == "NON_NULL"))
                obj[f.Name] = SampleValue(f.Type, depth + 1);
            return obj;
        }
        return "";
    }

    /// <summary>Field names suggested while typing inside a selection on <paramref name="typeName"/>.</summary>
    public IEnumerable<GraphQlField> FieldsOf(string typeName) =>
        Types.TryGetValue(typeName, out var type) ? type.Fields : [];
}
