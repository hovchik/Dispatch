using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Requests;

/// <summary>
/// Produces a copy of a request with every <c>{{variable}}</c> replaced, across all protocol settings, so executors
/// never deal with placeholders. Scripts, examples and descriptions are left untouched.
/// </summary>
public static class RequestResolver
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> Untouched = new(StringComparer.Ordinal)
    {
        nameof(ApiRequest.PreRequestScript),
        nameof(ApiRequest.TestScript),
        nameof(ApiRequest.Examples),
        nameof(ApiRequest.Description),
        nameof(ApiRequest.Name),
        nameof(ApiRequest.Folder),
        nameof(ApiRequest.Expectations)
    };

    public static ApiRequest Resolve(ApiRequest request, IReadOnlyDictionary<string, string> variables)
    {
        var source = JsonSerializer.SerializeToNode(request, Options)!.AsObject();
        var resolved = new JsonObject(source.Select(kv => KeyValuePair.Create(kv.Key,
            Untouched.Contains(kv.Key) ? kv.Value?.DeepClone() : Walk(kv.Value, variables))));
        var result = resolved.Deserialize<ApiRequest>(Options)!;
        // Stored snapshots are data, not templates: a body containing "{{" must not be altered.
        for (var i = 0; i < result.Assertions.Count && i < request.Assertions.Count; i++)
            if (request.Assertions[i].Source == ValueSource.Snapshot)
                result.Assertions[i].Expected = request.Assertions[i].Expected;
        return result;
    }

    /// <summary>Returns a resolved, detached copy of <paramref name="node"/>.</summary>
    private static JsonNode? Walk(JsonNode? node, IReadOnlyDictionary<string, string> variables) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(kv => KeyValuePair.Create(kv.Key, Walk(kv.Value, variables)))),
        JsonArray array => new JsonArray(array.Select(n => Walk(n, variables)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var s) && s.Contains("{{", StringComparison.Ordinal) =>
            JsonValue.Create(VariableResolver.Resolve(s, variables)),
        _ => node?.DeepClone()
    };
}
