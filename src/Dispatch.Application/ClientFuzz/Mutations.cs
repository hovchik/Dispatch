using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Impact;

namespace Dispatch.Application.ClientFuzz;

/// <summary>Sees traffic passing through the capture proxy and may change responses on their way to the client.</summary>
public interface IResponseInterceptor
{
    void OnRequest(string method, string url, string body, DateTimeOffset at);

    /// <summary>Returns a replacement for the response, or null to pass it through unchanged.</summary>
    InterceptResult? Intercept(InterceptedResponse response, DateTimeOffset at);
}

public sealed record InterceptedResponse(string Method, string Url, int Status, string? ContentType, string Body);

/// <summary>What the client receives instead: status, body, and how long to hold it back first.</summary>
public sealed record InterceptResult(int Status, string Reason, string Body, TimeSpan Delay, string Note);

public enum MutationKind
{
    NullField,
    DropField,
    EmptyString,
    WrongType,
    UnexpectedEnum,
    EmptyArray,
    SingleItem,
    HugeString,
    ExtraField,
    ServerError,
    Unavailable,
    RateLimited,
    Unauthorized,
    MalformedJson,
    EmptyBody,
    Slow
}

/// <summary>One change to make to a response. <see cref="Path"/> uses <c>[*]</c>; array paths change the first item.</summary>
public sealed record ResponseMutation(MutationKind Kind, string Path, string Description)
{
    public bool IsResponseLevel => Kind is MutationKind.ServerError or MutationKind.Unavailable or MutationKind.RateLimited
        or MutationKind.Unauthorized or MutationKind.MalformedJson or MutationKind.EmptyBody or MutationKind.Slow;
}

/// <summary>Plans which mutations to try on an endpoint, from one of its real responses, and applies them.</summary>
public static partial class Mutations
{
    /// <summary>
    /// Mutations worth trying for a response, most revealing first: failures of the whole response, then nulls and
    /// missing fields (the most common client crashes), empty / single-item lists, unexpected enum values, wrong types,
    /// unknown extra fields and very long strings.
    /// </summary>
    public static List<ResponseMutation> Plan(string body, IReadOnlySet<MutationKind> kinds, int max)
    {
        var plan = new List<ResponseMutation>
        {
            new(MutationKind.ServerError, "", "HTTP 500 Internal Server Error"),
            new(MutationKind.Unauthorized, "", "HTTP 401 Unauthorized (session expired)"),
            new(MutationKind.MalformedJson, "", "a truncated, malformed JSON body"),
            new(MutationKind.EmptyBody, "", "an empty 200 response body"),
            new(MutationKind.RateLimited, "", "HTTP 429 Too Many Requests (Retry-After: 2)"),
            new(MutationKind.Unavailable, "", "HTTP 503 Service Unavailable"),
            new(MutationKind.Slow, "", "a slow response")
        };

        if (Parse(body) is { } root)
        {
            var fields = new List<(List<string> Path, JsonNode? Value, string Key)>();
            var arrays = new List<(List<string> Path, JsonArray Array)>();
            Walk(root, [], fields, arrays);
            fields = fields.OrderBy(f => f.Path.Count).ToList();

            foreach (var f in fields)
                plan.Add(new(MutationKind.NullField, JsonShape.Join(f.Path), $"{JsonShape.Join(f.Path)} is null"));
            foreach (var f in fields)
                plan.Add(new(MutationKind.DropField, JsonShape.Join(f.Path), $"{JsonShape.Join(f.Path)} is missing"));
            foreach (var a in arrays)
            {
                plan.Add(new(MutationKind.EmptyArray, JsonShape.Join(a.Path), $"{JsonShape.Join(a.Path)} is an empty list"));
                if (a.Array.Count > 1)
                    plan.Add(new(MutationKind.SingleItem, JsonShape.Join(a.Path), $"{JsonShape.Join(a.Path)} has a single item"));
            }
            foreach (var f in fields.Where(f => f.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String && EnumKey().IsMatch(f.Key)))
                plan.Add(new(MutationKind.UnexpectedEnum, JsonShape.Join(f.Path), $"{JsonShape.Join(f.Path)} has an unexpected value"));
            foreach (var f in fields.Where(f => f.Value is JsonValue))
                plan.Add(new(MutationKind.WrongType, JsonShape.Join(f.Path), $"{JsonShape.Join(f.Path)} has the wrong type ({Describe(f.Value)} → {Flip(f.Value)})"));
            foreach (var f in fields.Where(f => f.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length > 0))
                plan.Add(new(MutationKind.EmptyString, JsonShape.Join(f.Path), $"{JsonShape.Join(f.Path)} is an empty string"));
            if (root is JsonObject)
                plan.Add(new(MutationKind.ExtraField, "$", "an unknown extra field"));
            if (fields.FirstOrDefault(f => TextKey().IsMatch(f.Key) && f.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String) is { Path: not null } text)
                plan.Add(new(MutationKind.HugeString, JsonShape.Join(text.Path), $"{JsonShape.Join(text.Path)} is 10,000 characters long"));
        }

        // Keep the order above, but make sure every enabled kind gets a turn before the cap.
        var allowed = plan.Where(m => kinds.Contains(m.Kind)).ToList();
        if (allowed.Count <= max)
            return allowed;
        var firstOfEach = allowed.GroupBy(m => m.Kind).Select(g => g.First()).ToList();
        return firstOfEach.Concat(allowed.Except(firstOfEach)).Take(max).OrderBy(allowed.IndexOf).ToList();
    }

    /// <summary>The changed response, or null when the mutation no longer applies (e.g. the field isn't in this response).</summary>
    public static InterceptResult? Apply(ResponseMutation mutation, InterceptedResponse response, TimeSpan slowDelay)
    {
        string Note() => mutation.Description;
        switch (mutation.Kind)
        {
            case MutationKind.ServerError:
                return new InterceptResult(500, "Internal Server Error", """{"error":"Internal Server Error"}""", TimeSpan.Zero, Note());
            case MutationKind.Unavailable:
                return new InterceptResult(503, "Service Unavailable", """{"error":"Service Unavailable"}""", TimeSpan.Zero, Note());
            case MutationKind.RateLimited:
                return new InterceptResult(429, "Too Many Requests", """{"error":"Too Many Requests"}""", TimeSpan.Zero, Note());
            case MutationKind.Unauthorized:
                return new InterceptResult(401, "Unauthorized", """{"error":"Unauthorized"}""", TimeSpan.Zero, Note());
            case MutationKind.MalformedJson:
                return response.Body.Length < 2 ? null
                    : new InterceptResult(response.Status, "OK", response.Body[..(response.Body.Length / 2)], TimeSpan.Zero, Note());
            case MutationKind.EmptyBody:
                return new InterceptResult(response.Status, "OK", "", TimeSpan.Zero, Note());
            case MutationKind.Slow:
                return new InterceptResult(response.Status, "OK", response.Body, slowDelay, Note());
        }

        if (Parse(response.Body) is not { } root)
            return null;
        if (mutation.Kind == MutationKind.ExtraField)
        {
            if (root is not JsonObject obj)
                return null;
            obj["_unexpectedField"] = new JsonObject { ["added"] = "by Dispatch client fuzzing", ["nested"] = new JsonArray(1, "two", null) };
            return new InterceptResult(response.Status, "OK", root.ToJsonString(), TimeSpan.Zero, Note());
        }

        var segments = JsonShape.Split(mutation.Path);
        if (segments.Count == 0 || Navigate(root, segments.Take(segments.Count - 1)) is not { } parent)
            return null;
        var last = segments[^1];
        JsonNode? Current() => last == JsonShape.Any ? (parent as JsonArray)?.FirstOrDefault() : (parent as JsonObject)?[last];
        void Set(JsonNode? value)
        {
            if (last == JsonShape.Any && parent is JsonArray array && array.Count > 0)
                array[0] = value;
            else if (parent is JsonObject obj)
                obj[last] = value;
        }

        var exists = last == JsonShape.Any ? parent is JsonArray { Count: > 0 } : parent is JsonObject o && o.ContainsKey(last);
        if (!exists)
            return null;
        switch (mutation.Kind)
        {
            case MutationKind.NullField:
                Set(null);
                break;
            case MutationKind.DropField:
                if (parent is JsonObject dropFrom)
                    dropFrom.Remove(last);
                else if (parent is JsonArray dropItem)
                    dropItem.RemoveAt(0);
                break;
            case MutationKind.EmptyString:
                Set("");
                break;
            case MutationKind.UnexpectedEnum:
                Set("UNEXPECTED_VALUE");
                break;
            case MutationKind.WrongType:
                Set(Flip(Current()));
                break;
            case MutationKind.HugeString:
                Set(new string('W', 10_000));
                break;
            case MutationKind.EmptyArray when Current() is JsonArray empty:
                empty.Clear();
                break;
            case MutationKind.SingleItem when Current() is JsonArray many && many.Count > 1:
                while (many.Count > 1)
                    many.RemoveAt(many.Count - 1);
                break;
            default:
                return null;
        }
        return new InterceptResult(response.Status, "OK", root.ToJsonString(), TimeSpan.Zero, Note());
    }

    private static JsonNode? Navigate(JsonNode node, IEnumerable<string> segments)
    {
        JsonNode? current = node;
        foreach (var s in segments)
            current = s == JsonShape.Any ? (current as JsonArray)?.FirstOrDefault() : (current as JsonObject)?[s];
        return current;
    }

    private static void Walk(JsonNode? node, List<string> path, List<(List<string>, JsonNode?, string)> fields, List<(List<string>, JsonArray)> arrays)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    List<string> child = [.. path, key];
                    if (value is not null)
                        fields.Add((child, value, key));
                    Walk(value, child, fields, arrays);
                }
                break;
            case JsonArray array:
                arrays.Add((path, array));
                if (array.FirstOrDefault() is { } first)
                    Walk(first, [.. path, JsonShape.Any], fields, arrays);
                break;
        }
    }

    private static string Describe(JsonNode? value) => value is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null"
    } : value is JsonArray ? "array" : "object";

    /// <summary>The same value as another type: numbers become strings, strings numbers, booleans strings.</summary>
    private static JsonNode? Flip(JsonNode? value) => value is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.Number => JsonValue.Create(v.ToJsonString()),
        JsonValueKind.String => JsonValue.Create(12345),
        JsonValueKind.True or JsonValueKind.False => JsonValue.Create(v.ToJsonString()),
        _ => JsonValue.Create(0)
    } : JsonValue.Create("not an object");

    internal static JsonNode? Parse(string text)
    {
        var t = text.TrimStart();
        if (!(t.StartsWith('{') || t.StartsWith('[')))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^(status|state|type|kind|role|level|mode|category|currency|plan|tier|stage|phase)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnumKey();

    [GeneratedRegex("(name|title|description|label|text|comment|bio|summary)", RegexOptions.IgnoreCase)]
    private static partial Regex TextKey();
}
