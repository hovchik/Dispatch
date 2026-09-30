using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Domain;

namespace Dispatch.Application.Mock;

public sealed record MockReply(int Status, string Body, string Note);

/// <summary>
/// In-memory resources for stateful mocks (like json-server): <c>POST /pets</c> stores an item and later
/// <c>GET /pets</c>, <c>GET /pets/{id}</c>, <c>PUT</c>, <c>PATCH</c> and <c>DELETE /pets/{id}</c> see it. Collections are
/// seeded from the list route's example. Routes whose resource was never touched fall back to their saved examples.
/// </summary>
public sealed class MockState(MockRouteTable routes, Func<ApiRequest, ResponseExample, string> render)
{
    private sealed class Resource
    {
        public List<JsonObject> Items { get; } = [];
        public bool Seeded { get; set; }
        public bool Touched { get; set; }

        /// <summary>The list example's envelope (e.g. <c>{"data":[...],"total":3}</c>) and the array's property.</summary>
        public JsonObject? Envelope { get; set; }
        public string? ArrayProperty { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        lock (_gate)
            _resources.Clear();
    }

    /// <summary>Handles a matched request statefully, or returns null to serve the route's example as usual.</summary>
    public MockReply? Handle(string method, MockMatch match, string body)
    {
        var segments = match.Template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;
        var isItem = IsParameter(segments[^1]);
        var collectionTemplate = "/" + string.Join("/", isItem ? segments[..^1] : segments);
        var key = Normalize(collectionTemplate);
        var idParameter = isItem ? ParameterName(segments[^1]) : null;
        var id = idParameter is not null && match.Variables.TryGetValue(idParameter, out var value) ? value : null;

        lock (_gate)
        {
            if (!_resources.TryGetValue(key, out var resource))
                _resources[key] = resource = new Resource();
            Seed(resource, key);

            switch (method.ToUpperInvariant())
            {
                case "GET" when !isItem:
                    return resource.Touched ? new MockReply(200, ListBody(resource), "stateful list") : null;
                case "POST" when !isItem:
                {
                    if (Parse(body) is not JsonObject input)
                        return new MockReply(400, Error("Request body must be a JSON object."), "stateful create: bad body");
                    var item = BaseItem(match) ?? new JsonObject();
                    foreach (var (k, v) in input)
                        item[k] = v?.DeepClone();
                    var idKey = IdKey(item, idParameter ?? SingularId(collectionTemplate)) ?? "id";
                    if (item[idKey] is null || item[idKey]?.ToString() is "" or "0")
                        item[idKey] = NextId(resource, idKey);
                    resource.Items.Add(item);
                    resource.Touched = true;
                    var status = match.Example is { StatusCode: >= 200 and < 300 } e ? e.StatusCode : 201;
                    return new MockReply(status, item.ToJsonString(), "stateful create");
                }
                case "GET" or "PUT" or "PATCH" or "DELETE" when isItem && id is not null:
                {
                    var index = resource.Items.FindIndex(i => IdOf(i, idParameter) == id);
                    if (index < 0)
                        return resource.Touched ? new MockReply(404, Error($"No item with id {id}."), "stateful: not found") : null;
                    var item = resource.Items[index];
                    switch (method.ToUpperInvariant())
                    {
                        case "GET":
                            return new MockReply(200, item.ToJsonString(), "stateful read");
                        case "DELETE":
                            resource.Items.RemoveAt(index);
                            return new MockReply(204, "", "stateful delete");
                        default:
                            if (Parse(body) is not JsonObject changes)
                                return new MockReply(400, Error("Request body must be a JSON object."), "stateful update: bad body");
                            var idKey = IdKey(item, idParameter) ?? "id";
                            var updated = method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ? new JsonObject() : (JsonObject)item.DeepClone();
                            foreach (var (k, v) in changes)
                                updated[k] = v?.DeepClone();
                            updated[idKey] = item[idKey]?.DeepClone();
                            resource.Items[index] = updated;
                            return new MockReply(200, updated.ToJsonString(), "stateful update");
                    }
                }
                default:
                    return null;
            }
        }
    }

    private void Seed(Resource resource, string key)
    {
        if (resource.Seeded)
            return;
        resource.Seeded = true;
        var list = routes.Routes.FirstOrDefault(r => r.Method == "GET" && Normalize(r.Template) == key);
        var example = list?.Request.Examples.FirstOrDefault(e => e.StatusCode is >= 200 and < 300);
        if (list is null || example is null)
            return;
        var node = Parse(render(list.Request, example));
        JsonArray? array = node as JsonArray;
        if (node is JsonObject envelope)
        {
            var property = envelope.Where(p => p.Value is JsonArray).OrderByDescending(p => ((JsonArray)p.Value!).Count).FirstOrDefault();
            if (property.Value is JsonArray inner)
            {
                array = inner;
                resource.Envelope = envelope;
                resource.ArrayProperty = property.Key;
            }
        }
        if (array is null)
            return;
        resource.Items.AddRange(array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
        resource.Touched = resource.Items.Count > 0;
    }

    private string ListBody(Resource resource)
    {
        var items = new JsonArray(resource.Items.Select(i => (JsonNode?)i.DeepClone()).ToArray());
        if (resource.Envelope is null || resource.ArrayProperty is null)
            return items.ToJsonString();
        var envelope = (JsonObject)resource.Envelope.DeepClone();
        envelope[resource.ArrayProperty] = items;
        foreach (var countKey in new[] { "total", "count", "totalCount", "total_count" })
            if (envelope[countKey] is JsonValue)
                envelope[countKey] = resource.Items.Count;
        return envelope.ToJsonString();
    }

    /// <summary>The POST route's example as a starting point, so server-set fields (createdAt, status) are present.</summary>
    private JsonObject? BaseItem(MockMatch match) =>
        match.Example is { } example && Parse(render(match.Request, example)) is JsonObject obj ? obj : null;

    private static JsonNode NextId(Resource resource, string idKey)
    {
        var ids = resource.Items.Select(i => i[idKey]).Where(v => v is not null).ToList();
        if (ids.Count > 0 && ids.All(v => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number))
            return JsonValue.Create(ids.Max(v => v!.GetValue<double>()) is var max ? (long)max + 1 : 1);
        if (ids.Count > 0)
            return JsonValue.Create(Guid.NewGuid().ToString());
        return JsonValue.Create(1L);
    }

    private static string? IdKey(JsonObject item, string? parameter)
    {
        foreach (var candidate in new[] { "id", parameter, "_id", "uuid" })
            if (candidate is not null && item.ContainsKey(candidate))
                return candidate;
        return item.Select(p => p.Key).FirstOrDefault(k => k.EndsWith("Id", StringComparison.Ordinal) || k.EndsWith("_id", StringComparison.Ordinal));
    }

    private static string? IdOf(JsonObject item, string? parameter) =>
        IdKey(item, parameter) is { } key ? item[key] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            _ => null
        } : null;

    private static string SingularId(string template)
    {
        var last = template.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "item";
        return (last.EndsWith('s') ? last[..^1] : last) + "Id";
    }

    private static bool IsParameter(string segment) =>
        segment.StartsWith('{') || segment.StartsWith(':');

    private static string ParameterName(string segment) => segment.Trim('{', '}', ':', ' ');

    private static string Normalize(string template) =>
        "/" + string.Join("/", template.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => IsParameter(s) ? "{}" : s.ToLowerInvariant()));

    private static JsonNode? Parse(string text)
    {
        try
        {
            return text.Trim().Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();
}
