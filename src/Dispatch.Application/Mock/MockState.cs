using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Domain;

namespace Dispatch.Application.Mock;

public sealed record MockReply(int Status, string Body, string Note, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// In-memory resources for stateful mocks (like json-server): <c>POST /pets</c> stores an item and later
/// <c>GET /pets</c>, <c>GET /pets/{id}</c>, <c>PUT</c>, <c>PATCH</c> and <c>DELETE /pets/{id}</c> see it. Collections are
/// seeded from the list route's example. Routes whose resource was never touched fall back to their saved examples.
/// <para>
/// Nested resources belong to their parent: <c>POST /users/7/orders</c> stamps <c>userId: 7</c> on the order and
/// <c>GET /users/7/orders</c> lists only that user's orders. List routes understand filters (<c>?status=sold</c>,
/// <c>price_gte=10</c>, <c>name_like=re</c>, <c>q=text</c>), sorting (<c>sort=-price,name</c> or <c>_sort</c>/<c>_order</c>)
/// and paging (<c>page</c>/<c>limit</c>, <c>_page</c>/<c>_limit</c>, <c>offset</c>), and report <c>X-Total-Count</c>.
/// </para>
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

    private static readonly HashSet<string> ReservedQuery = new(StringComparer.OrdinalIgnoreCase)
    {
        "q", "page", "limit", "offset", "sort", "order", "per_page", "perpage", "pagesize", "page_size",
        "_page", "_limit", "_per_page", "_start", "_end", "_sort", "_order"
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        lock (_gate)
            _resources.Clear();
    }

    /// <summary>Every collection with its items, as <c>{"/pets":[…],"/users/{}/orders":[…]}</c>; keys are the list templates.</summary>
    public JsonObject Export()
    {
        lock (_gate)
        {
            var result = new JsonObject();
            foreach (var (key, resource) in _resources.OrderBy(r => r.Key, StringComparer.Ordinal))
                if (resource.Touched || resource.Items.Count > 0)
                    result[key] = new JsonArray(resource.Items.Select(i => (JsonNode?)i.DeepClone()).ToArray());
            return result;
        }
    }

    /// <summary>
    /// Loads collections from a json-server style document: <c>{"pets":[…],"users/{id}/orders":[…]}</c> (or the output
    /// of <see cref="Export"/>). Loaded collections replace what the store held and are served instead of the examples.
    /// </summary>
    public int Import(JsonObject data)
    {
        var loaded = 0;
        lock (_gate)
        {
            foreach (var (name, node) in data)
            {
                if (node is not JsonArray array)
                    continue;
                var resource = new Resource { Seeded = true, Touched = true };
                resource.Items.AddRange(array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
                _resources[Normalize("/" + name.TrimStart('/'))] = resource;
                loaded++;
            }
        }
        return loaded;
    }

    /// <summary>Handles a matched request statefully, or returns null to serve the route's example as usual.</summary>
    public MockReply? Handle(string method, MockMatch match, string body) =>
        Handle(method, match, body, new Dictionary<string, string>(), null);

    /// <summary>
    /// Handles a matched request statefully, or returns null to serve the route's example as usual. <paramref name="query"/>
    /// drives list filtering, sorting and paging; <paramref name="path"/> is used for the <c>Location</c> of created items.
    /// </summary>
    public MockReply? Handle(string method, MockMatch match, string body, IReadOnlyDictionary<string, string> query, string? path)
    {
        var segments = match.Template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;
        var isItem = IsParameter(segments[^1]);
        var collectionTemplate = "/" + string.Join("/", isItem ? segments[..^1] : segments);
        var key = Normalize(collectionTemplate);
        var idParameter = isItem ? ParameterName(segments[^1]) : null;
        var id = idParameter is not null && match.Variables.TryGetValue(idParameter, out var value) ? value : null;
        // Parent ids from the path: /users/{userId}/orders → userId = 7.
        var parents = match.PathVariables.Where(p => p.Key != idParameter).ToList();

        lock (_gate)
        {
            if (!_resources.TryGetValue(key, out var resource))
                _resources[key] = resource = new Resource();
            Seed(resource, key, parents);

            switch (method.ToUpperInvariant())
            {
                case "GET" or "HEAD" when !isItem:
                {
                    if (!resource.Touched)
                        return null;
                    var scoped = resource.Items.Where(i => BelongsTo(i, parents)).ToList();
                    var (page, total) = ListQuery.Apply(scoped, query);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Total-Count"] = total.ToString(CultureInfo.InvariantCulture)
                    };
                    var note = total == scoped.Count && page.Count == scoped.Count ? "stateful list" : $"stateful list: {page.Count} of {total}";
                    return new MockReply(200, ListBody(resource, page, total), note, headers);
                }
                case "POST" when !isItem:
                {
                    if (Parse(body) is not JsonObject input)
                        return new MockReply(400, Error("Request body must be a JSON object."), "stateful create: bad body");
                    var item = BaseItem(match) ?? new JsonObject();
                    foreach (var (k, v) in input)
                        item[k] = v?.DeepClone();
                    var idKey = IdKey(item, idParameter ?? SingularId(collectionTemplate), parents) ?? "id";
                    foreach (var (parentKey, parentValue) in parents)
                        if (item[parentKey] is null)
                            item[parentKey] = Typed(parentValue);
                    if (item[idKey] is null || item[idKey]?.ToString() is "" or "0")
                        item[idKey] = NextId(resource, idKey);
                    else if (resource.Items.Any(i => ValueText(i[idKey]) == ValueText(item[idKey])))
                        return new MockReply(409, Error($"An item with {idKey} {ValueText(item[idKey])} already exists."), "stateful create: conflict");
                    resource.Items.Add(item);
                    resource.Touched = true;
                    var status = match.Example is { StatusCode: >= 200 and < 300 } e ? e.StatusCode : 201;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (path is { Length: > 0 } && ValueText(item[idKey]) is { Length: > 0 } created)
                        headers["Location"] = path.TrimEnd('/') + "/" + Uri.EscapeDataString(created);
                    return new MockReply(status, item.ToJsonString(), "stateful create", headers);
                }
                case "GET" or "HEAD" or "PUT" or "PATCH" or "DELETE" when isItem && id is not null:
                {
                    var index = resource.Items.FindIndex(i => IdOf(i, idParameter, parents) == id && BelongsTo(i, parents));
                    if (index < 0)
                        return resource.Touched ? new MockReply(404, Error($"No item with id {id}."), "stateful: not found") : null;
                    var item = resource.Items[index];
                    switch (method.ToUpperInvariant())
                    {
                        case "GET" or "HEAD":
                            return new MockReply(200, item.ToJsonString(), "stateful read");
                        case "DELETE":
                            resource.Items.RemoveAt(index);
                            return new MockReply(204, "", "stateful delete");
                        default:
                            if (Parse(body) is not JsonObject changes)
                                return new MockReply(400, Error("Request body must be a JSON object."), "stateful update: bad body");
                            var idKey = IdKey(item, idParameter, parents) ?? "id";
                            var updated = method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ? new JsonObject() : (JsonObject)item.DeepClone();
                            foreach (var (k, v) in changes)
                                updated[k] = v?.DeepClone();
                            updated[idKey] = item[idKey]?.DeepClone();
                            // The parent link survives a PUT, like the id does.
                            foreach (var (parentKey, _) in parents)
                                if (updated[parentKey] is null && item[parentKey] is { } parent)
                                    updated[parentKey] = parent.DeepClone();
                            resource.Items[index] = updated;
                            return new MockReply(200, updated.ToJsonString(), "stateful update");
                    }
                }
                default:
                    return null;
            }
        }
    }

    private void Seed(Resource resource, string key, IReadOnlyList<KeyValuePair<string, string>> parents)
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
        foreach (var item in array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()))
        {
            // Seeded items belong to the parent whose list was asked for first (the example rarely says).
            foreach (var (parentKey, parentValue) in parents)
                if (item[parentKey] is null)
                    item[parentKey] = Typed(parentValue);
            resource.Items.Add(item);
        }
        resource.Touched = resource.Items.Count > 0;
    }

    private static string ListBody(Resource resource, IReadOnlyList<JsonObject> page, int total)
    {
        var items = new JsonArray(page.Select(i => (JsonNode?)i.DeepClone()).ToArray());
        if (resource.Envelope is null || resource.ArrayProperty is null)
            return items.ToJsonString();
        var envelope = (JsonObject)resource.Envelope.DeepClone();
        envelope[resource.ArrayProperty] = items;
        foreach (var countKey in new[] { "total", "count", "totalCount", "total_count" })
            if (envelope[countKey] is JsonValue)
                envelope[countKey] = total;
        return envelope.ToJsonString();
    }

    /// <summary>The POST route's example as a starting point, so server-set fields (createdAt, status) are present.</summary>
    private JsonObject? BaseItem(MockMatch match) =>
        match.Example is { } example && Parse(render(match.Request, example)) is JsonObject obj ? obj : null;

    /// <summary>An item belongs to the parents in the path when it carries their ids, or says nothing about them.</summary>
    private static bool BelongsTo(JsonObject item, IReadOnlyList<KeyValuePair<string, string>> parents) =>
        parents.All(p => item[p.Key] is not { } linked || ValueText(linked) == p.Value);

    private static JsonNode NextId(Resource resource, string idKey)
    {
        var ids = resource.Items.Select(i => i[idKey]).Where(v => v is not null).ToList();
        if (ids.Count > 0 && ids.All(v => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number))
            // Ids may be JsonElement-backed (from parsed examples) or JsonValue<long> (from an earlier POST);
            // GetValue<double>() throws for the latter, so go through the JSON text.
            return JsonValue.Create((long)ids.Max(v => double.Parse(v!.ToJsonString(), CultureInfo.InvariantCulture)) + 1);
        if (ids.Count > 0)
            return JsonValue.Create(Guid.NewGuid().ToString());
        return JsonValue.Create(1L);
    }

    /// <summary>The property holding an item's own id; parent links (<c>userId</c> on an order) never count.</summary>
    private static string? IdKey(JsonObject item, string? parameter, IReadOnlyList<KeyValuePair<string, string>> parents)
    {
        foreach (var candidate in new[] { "id", parameter, "_id", "uuid" })
            if (candidate is not null && item.ContainsKey(candidate))
                return candidate;
        return item.Select(p => p.Key).FirstOrDefault(k => (k.EndsWith("Id", StringComparison.Ordinal) || k.EndsWith("_id", StringComparison.Ordinal))
                                                           && !parents.Any(parent => parent.Key == k));
    }

    private static string? IdOf(JsonObject item, string? parameter, IReadOnlyList<KeyValuePair<string, string>> parents) =>
        IdKey(item, parameter, parents) is { } key ? ValueText(item[key]) : null;

    /// <summary>A value as the text a URL would carry: strings as they are, numbers and booleans as JSON.</summary>
    internal static string? ValueText(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null
    };

    /// <summary>A path value as JSON: numbers stay numbers (<c>7</c>), everything else is a string.</summary>
    private static JsonNode Typed(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(text);

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

    /// <summary>Filtering, full-text search, sorting and paging of a stateful list from its query string.</summary>
    internal static class ListQuery
    {
        public static (List<JsonObject> Page, int Total) Apply(List<JsonObject> items, IReadOnlyDictionary<string, string> query)
        {
            IEnumerable<JsonObject> result = items;
            foreach (var (rawKey, value) in query)
            {
                if (ReservedQuery.Contains(rawKey))
                    continue;
                var (key, op) = Operator(rawKey);
                // Only keys that name a field of some item filter; unknown keys (expand=1, _t=…) are ignored.
                if (!items.Any(i => Field(i, key) is not null))
                    continue;
                result = result.Where(i => Compare(Field(i, key), op, value));
            }
            if (query.GetValueOrDefault("q") is { Length: > 0 } text)
                result = result.Where(i => ContainsText(i, text));

            var sort = query.GetValueOrDefault("sort") ?? query.GetValueOrDefault("_sort");
            if (sort is { Length: > 0 })
            {
                var descendingAll = (query.GetValueOrDefault("order") ?? query.GetValueOrDefault("_order"))?.StartsWith("desc", StringComparison.OrdinalIgnoreCase) == true;
                IOrderedEnumerable<JsonObject>? ordered = null;
                foreach (var spec in sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var field = spec;
                    var descending = descendingAll;
                    if (field.StartsWith('-'))
                        (field, descending) = (field[1..], true);
                    else if (field.StartsWith('+'))
                        field = field[1..];
                    else if (field.Split(':', ' ') is [var f, var dir])
                        (field, descending) = (f, dir.StartsWith("desc", StringComparison.OrdinalIgnoreCase));
                    var name = field;
                    var comparer = new FieldComparer(name);
                    ordered = ordered is null
                        ? descending ? result.OrderByDescending(i => i, comparer) : result.OrderBy(i => i, comparer)
                        : descending ? ordered.ThenByDescending(i => i, comparer) : ordered.ThenBy(i => i, comparer);
                }
                if (ordered is not null)
                    result = ordered;
            }

            var filtered = result.ToList();
            var total = filtered.Count;
            var limit = Int(query, "limit", "_limit", "per_page", "_per_page", "perPage", "pageSize", "page_size");
            var offset = Int(query, "offset", "_start");
            var page = Int(query, "page", "_page");
            if (query.ContainsKey("_end") && Int(query, "_end") is { } end)
                limit = Math.Max(0, end - (offset ?? 0));
            if (page is { } p && limit is { } size)
                offset = Math.Max(0, p - 1) * size;
            if (page is not null && limit is null)
                (offset, limit) = (Math.Max(0, page.Value - 1) * 10, 10);
            if (offset is null && limit is null)
                return (filtered, total);
            return (filtered.Skip(Math.Max(0, offset ?? 0)).Take(Math.Max(0, limit ?? int.MaxValue)).ToList(), total);
        }

        private static int? Int(IReadOnlyDictionary<string, string> query, params string[] names)
        {
            foreach (var name in names)
                if (query.TryGetValue(name, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    return value;
            return null;
        }

        private static (string Key, string Op) Operator(string key)
        {
            foreach (var suffix in new[] { "_ne", "_gte", "_lte", "_gt", "_lt", "_like" })
                if (key.Length > suffix.Length && key.EndsWith(suffix, StringComparison.Ordinal))
                    return (key[..^suffix.Length], suffix[1..]);
            return (key, "eq");
        }

        /// <summary>A top-level field, or a dotted path (<c>address.city</c>); names match exactly, then ignoring case.</summary>
        private static JsonNode? Field(JsonObject item, string path)
        {
            JsonNode? node = item;
            foreach (var part in path.Split('.'))
            {
                if (node is not JsonObject obj)
                    return null;
                if (!obj.ContainsKey(part))
                {
                    var key = obj.Select(p => p.Key).FirstOrDefault(k => k.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (key is null)
                        return null;
                    node = obj[key];
                }
                else
                    node = obj[part];
            }
            return node;
        }

        private static bool Compare(JsonNode? field, string op, string expected)
        {
            if (field is JsonArray array)
                return op is "eq" or "like" ? array.Any(e => Compare(e, op, expected)) : array.All(e => Compare(e, op, expected));
            var actual = ValueText(field) ?? field?.ToJsonString() ?? "null";
            if (op == "like")
                return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
            var numeric = double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                          & double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var b);
            var comparison = numeric ? a.CompareTo(b) : string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
            return op switch
            {
                "eq" => numeric ? comparison == 0 : actual == expected || comparison == 0,
                "ne" => numeric ? comparison != 0 : actual != expected && comparison != 0,
                "gt" => comparison > 0,
                "gte" => comparison >= 0,
                "lt" => comparison < 0,
                "lte" => comparison <= 0,
                _ => false
            };
        }

        private static bool ContainsText(JsonNode? node, string text) => node switch
        {
            JsonObject o => o.Any(p => ContainsText(p.Value, text)),
            JsonArray a => a.Any(e => ContainsText(e, text)),
            JsonValue v => (ValueText(v) ?? "").Contains(text, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        private sealed class FieldComparer(string field) : IComparer<JsonObject>
        {
            public int Compare(JsonObject? x, JsonObject? y)
            {
                var a = x is null ? null : Field(x, field);
                var b = y is null ? null : Field(y, field);
                if (a is null || b is null)
                    return (a is null ? 0 : 1) - (b is null ? 0 : 1);
                if (a is JsonValue va && b is JsonValue vb && va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
                    return double.Parse(va.ToJsonString(), CultureInfo.InvariantCulture).CompareTo(double.Parse(vb.ToJsonString(), CultureInfo.InvariantCulture));
                return string.Compare(ValueText(a) ?? a.ToJsonString(), ValueText(b) ?? b.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
