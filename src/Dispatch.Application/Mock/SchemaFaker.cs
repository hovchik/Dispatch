using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Variables;

namespace Dispatch.Application.Mock;

/// <summary>
/// Generates realistic JSON from a JSON Schema / OpenAPI schema: honours type, format, enum, const, bounds, lengths,
/// item counts, required properties, nullable and allOf / oneOf / anyOf, and picks fake data by property name
/// (<c>email</c>, <c>firstName</c>, <c>city</c>, <c>price</c>, <c>createdAt</c>, ...).
/// </summary>
public sealed partial class SchemaFaker(Faker? faker = null, Random? random = null)
{
    private const int MaxDepth = 8;
    private readonly Faker _faker = faker ?? Faker.Shared;
    private readonly Random _r = random ?? Random.Shared;
    private int _sequence;

    /// <summary>Generates a value for a schema. Local <c>$ref</c>s (<c>#/components/…</c>, <c>#/$defs/…</c>) are resolved against the schema itself.</summary>
    public JsonNode? Generate(JsonNode? schema) =>
        Generate(schema is JsonObject && schema.ToJsonString().Contains("\"$ref\"", StringComparison.Ordinal) ? Inline(schema, schema) : schema, null, 0);

    /// <summary>Generates a value shaped like <paramref name="sample"/> (types and keys) with fresh data.</summary>
    public JsonNode? GenerateLike(JsonNode? sample) => Generate(InferSchema(sample), null, 0);

    private JsonNode? Generate(JsonNode? schema, string? name, int depth)
    {
        if (schema is not JsonObject s || depth > MaxDepth)
            return depth > MaxDepth ? null : ByName(name) ?? JsonValue.Create(_faker.Word());

        if (s["const"] is { } constant)
            return constant.DeepClone();
        if (s["enum"] is JsonArray { Count: > 0 } values)
            return values[_r.Next(values.Count)]?.DeepClone();

        if (s["allOf"] is JsonArray allOf)
        {
            var merged = new JsonObject();
            foreach (var part in allOf)
                if (Generate(part, name, depth + 1) is JsonObject o)
                    foreach (var (k, v) in o)
                        merged[k] = v?.DeepClone();
            return merged;
        }
        if ((s["oneOf"] ?? s["anyOf"]) is JsonArray { Count: > 0 } choices)
            return Generate(choices[_r.Next(choices.Count)], name, depth + 1);

        var type = s["type"] switch
        {
            JsonArray types => types.Select(t => t?.ToString()).FirstOrDefault(t => t != "null"),
            JsonValue t => t.ToString(),
            _ when s["properties"] is not null => "object",
            _ when s["items"] is not null => "array",
            _ => null
        };

        switch (type)
        {
            case "object":
                var obj = new JsonObject();
                var required = (s["required"] as JsonArray)?.Select(r => r?.ToString()).ToHashSet() ?? [];
                foreach (var (property, propertySchema) in s["properties"] as JsonObject ?? [])
                {
                    // Optional properties are usually present; occasionally left out like real APIs do.
                    if (!required.Contains(property) && required.Count > 0 && _r.Next(10) == 0)
                        continue;
                    obj[property] = Generate(propertySchema, property, depth + 1);
                }
                if (obj.Count == 0 && s["additionalProperties"] is JsonObject additional)
                    for (var i = 0; i < 2; i++)
                        obj[_faker.Word() + i] = Generate(additional, null, depth + 1);
                return obj;
            case "array":
                var min = Int(s["minItems"]) ?? 1;
                var max = Int(s["maxItems"]) ?? Math.Max(min, 5);
                var count = _r.Next(min, Math.Max(min, max) + 1);
                var array = new JsonArray();
                var singular = Singular(name);
                for (var i = 0; i < count; i++)
                    array.Add(Generate(s["items"], singular, depth + 1));
                if (s["uniqueItems"]?.GetValue<bool>() == true)
                    array = new JsonArray(array.GroupBy(n => n?.ToJsonString()).Select(g => g.First()?.DeepClone()).ToArray());
                return array;
            case "integer":
                return JsonValue.Create(Integer(s, name));
            case "number":
                return JsonValue.Create(Number(s, name));
            case "boolean":
                return JsonValue.Create(_r.Next(2) == 0);
            case "string":
                return JsonValue.Create(Text(s, name));
            case "null":
                return null;
            default:
                return ByName(name) ?? s["example"]?.DeepClone() ?? (s["examples"] as JsonArray)?.FirstOrDefault()?.DeepClone()
                       ?? s["default"]?.DeepClone() ?? JsonValue.Create(_faker.Word());
        }
    }

    private static int? Int(JsonNode? node) => Double(node) is { } d ? (int)d : null;

    // Works for parsed JSON and for values created from CLR numbers (which TryGetValue<double> won't convert).
    private static double? Double(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;

    private long Integer(JsonObject s, string? name)
    {
        var key = Normalize(name);
        var min = Double(s["minimum"]) ?? (Double(s["exclusiveMinimum"]) is { } em ? em + 1 : (double?)null);
        var max = Double(s["maximum"]) ?? (Double(s["exclusiveMaximum"]) is { } eM ? eM - 1 : (double?)null);
        if (min is null && max is null)
        {
            if (key is "id" || key.EndsWith("id"))
                return ++_sequence + _r.Next(0, 1000) * 10;
            if (key.Contains("age")) (min, max) = (18, 90);
            else if (key.Contains("year")) (min, max) = (1990, DateTime.UtcNow.Year);
            else if (key.Contains("quantity") || key.Contains("count") || key.Contains("stock")) (min, max) = (0, 100);
            else if (key.Contains("page")) (min, max) = (1, 10);
            else (min, max) = (1, 1000);
        }
        var low = (long)(min ?? 0);
        var high = (long)(max ?? low + 1000);
        var value = low >= high ? low : low + (long)(_r.NextDouble() * (high - low + 1));
        if (Double(s["multipleOf"]) is { } step and > 0)
            value = (long)(Math.Round(value / step) * step);
        return Math.Clamp(value, low, Math.Max(low, high));
    }

    private double Number(JsonObject s, string? name)
    {
        var key = Normalize(name);
        var min = Double(s["minimum"]) ?? Double(s["exclusiveMinimum"]);
        var max = Double(s["maximum"]) ?? Double(s["exclusiveMaximum"]);
        if (min is null && max is null)
        {
            if (key.Contains("lat")) (min, max) = (-90, 90);
            else if (key.Contains("lon") || key.Contains("lng")) (min, max) = (-180, 180);
            else if (key.Contains("rate") || key.Contains("ratio") || key.Contains("percent")) (min, max) = (0, 1);
            else (min, max) = (1, 1000);
        }
        var value = (min ?? 0) + _r.NextDouble() * ((max ?? (min ?? 0) + 1000) - (min ?? 0));
        return Math.Round(value, key.Contains("lat") || key.Contains("lon") || key.Contains("lng") ? 5 : 2);
    }

    private string Text(JsonObject s, string? name)
    {
        var format = s["format"]?.ToString();
        var value = format switch
        {
            "date-time" => _faker.Date(-365, 0).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            "date" => _faker.Date(-365, 0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => $"{_r.Next(24):00}:{_r.Next(60):00}:{_r.Next(60):00}",
            "email" or "idn-email" => _faker.Email(),
            "uuid" => Guid.NewGuid().ToString(),
            "uri" or "url" or "iri" => _faker.Url(),
            "hostname" or "idn-hostname" => _faker.DomainName(),
            "ipv4" => _faker.Ipv4(),
            "ipv6" => _faker.Generate("$randomIPV6")!,
            "byte" => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_faker.Word())),
            "binary" => _faker.AlphaNumeric(16),
            "password" => _faker.AlphaNumeric(12),
            "phone" => _faker.Phone(),
            _ => null
        } ?? ByName(name)?.ToString() ?? FromPattern(s["pattern"]?.ToString()) ?? _faker.LoremWords(_r.Next(1, 4));

        var minLength = Int(s["minLength"]) ?? 0;
        var maxLength = Int(s["maxLength"]);
        while (value.Length < minLength)
            value += _faker.AlphaNumeric(Math.Min(16, minLength - value.Length));
        if (maxLength is { } limit && value.Length > limit)
            value = value[..Math.Max(0, limit)];
        return value;
    }

    /// <summary>Simple patterns only: character classes with counts, e.g. <c>^[A-Z]{3}-\d{4}$</c>.</summary>
    private string? FromPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > 80)
            return null;
        var sb = new System.Text.StringBuilder();
        var tokens = PatternToken().Matches(pattern.Trim('^', '$'));
        if (tokens.Sum(t => t.Length) != pattern.Trim('^', '$').Length)
            return null;
        foreach (Match token in tokens)
        {
            var atom = token.Groups[1].Value;
            var count = token.Groups[2].Success ? int.Parse(token.Groups[2].Value, CultureInfo.InvariantCulture)
                : token.Groups[4].Value == "+" ? _r.Next(1, 6) : token.Groups[4].Value == "*" ? _r.Next(0, 5) : token.Groups[4].Value == "?" ? _r.Next(2) : 1;
            if (token.Groups[3].Success)
                count = _r.Next(count, int.Parse(token.Groups[3].Value, CultureInfo.InvariantCulture) + 1);
            for (var i = 0; i < count; i++)
                sb.Append(atom switch
                {
                    @"\d" or "[0-9]" => (char)('0' + _r.Next(10)),
                    "[A-Z]" => (char)('A' + _r.Next(26)),
                    "[a-z]" => (char)('a' + _r.Next(26)),
                    "[A-Za-z]" or "[a-zA-Z]" or @"\w" => _faker.AlphaNumeric(1)[0] is var c && char.IsDigit(c) ? 'x' : c,
                    "[A-Za-z0-9]" or "[a-zA-Z0-9]" => _faker.AlphaNumeric(1)[0],
                    _ when atom.Length == 1 || (atom.Length == 2 && atom[0] == '\\') => atom[^1],
                    _ => 'x'
                });
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(\\d|\\w|\[[^\]]+\]|\\.|[^\\\[\]{}()*+?|])(?:\{(\d+)(?:,(\d+))?\}|([*+?]))?")]
    private static partial Regex PatternToken();

    private static string Normalize(string? name) => (name ?? "").Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static string? Singular(string? name) =>
        name is null ? null : name.EndsWith("ies") ? name[..^3] + "y" : name.EndsWith('s') && name.Length > 1 ? name[..^1] : name;

    /// <summary>Fake data picked by property name, or null when the name says nothing useful.</summary>
    private JsonNode? ByName(string? name)
    {
        var key = Normalize(name);
        if (key.Length == 0)
            return null;
        string? text = key switch
        {
            "email" or "mail" or "emailaddress" or "useremail" => _faker.Email(),
            "firstname" or "givenname" => _faker.FirstName(),
            "lastname" or "surname" or "familyname" => _faker.LastName(),
            "name" or "fullname" or "displayname" or "author" or "owner" or "customer" or "contact" => _faker.FullName(),
            "username" or "login" or "handle" or "nickname" => _faker.UserName(),
            "phone" or "phonenumber" or "mobile" or "tel" or "telephone" => _faker.Phone(),
            "city" or "town" => _faker.City(),
            "country" => _faker.Country(),
            "countrycode" => _faker.CountryCode(),
            "street" or "address" or "streetaddress" or "address1" or "line1" => _faker.StreetAddress(),
            "zip" or "zipcode" or "postcode" or "postalcode" => _faker.ZipCode(),
            "company" or "companyname" or "organization" or "organisation" or "employer" => _faker.CompanyName(),
            "jobtitle" or "position" => _faker.JobTitle(),
            "title" or "subject" or "headline" => Capitalize(_faker.LoremWords(_r.Next(2, 5))),
            "description" or "summary" or "bio" or "about" or "comment" or "body" or "content" or "message" or "text" or "notes" or "note" => _faker.Sentence(),
            "url" or "website" or "homepage" or "link" or "href" => _faker.Url(),
            "avatar" or "avatarurl" or "image" or "imageurl" or "photo" or "photourl" or "picture" or "thumbnail" => $"https://picsum.photos/seed/{_faker.AlphaNumeric(6)}/200",
            "currency" or "currencycode" => _faker.CurrencyCode(),
            "color" or "colour" => _faker.ColorName(),
            "ip" or "ipaddress" => _faker.Ipv4(),
            "token" or "accesstoken" or "apikey" or "secret" => _faker.AlphaNumeric(32),
            "password" => _faker.AlphaNumeric(12),
            "status" or "state" => _faker.Pick(new[] { "active", "pending", "inactive" }),
            "product" or "productname" or "item" or "itemname" => _faker.ProductName(),
            "category" or "department" => _faker.Department(),
            "tag" or "label" or "keyword" => _faker.Word(),
            "slug" => string.Join("-", _faker.LoremWords(3).Split(' ')),
            "uuid" or "guid" => Guid.NewGuid().ToString(),
            "sku" => $"{_faker.AlphaNumeric(3).ToUpperInvariant()}-{_faker.Digits(5)}",
            _ when key.EndsWith("email") => _faker.Email(),
            _ when key.EndsWith("url") => _faker.Url(),
            _ when name!.EndsWith("At", StringComparison.Ordinal) || name.EndsWith("_at", StringComparison.OrdinalIgnoreCase)
                   || key.EndsWith("date") || key.EndsWith("time") || key.EndsWith("timestamp") =>
                _faker.Date(-365, 0).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            _ when key.EndsWith("name") => Capitalize(_faker.Word()),
            _ when key == "id" || key.EndsWith("id") => Guid.NewGuid().ToString(),
            _ => null
        };
        if (text is not null)
            return JsonValue.Create(text);
        return key switch
        {
            "price" or "amount" or "cost" or "total" or "subtotal" or "balance" or "salary" => JsonValue.Create(Math.Round(1 + _r.NextDouble() * 999, 2)),
            _ => null
        };
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A JSON Schema describing <paramref name="sample"/> (for data that only has an example, not a schema).</summary>
    public static JsonObject InferSchema(JsonNode? sample)
    {
        switch (sample)
        {
            case JsonObject obj:
                var properties = new JsonObject();
                foreach (var (k, v) in obj)
                    properties[k] = InferSchema(v);
                return new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(obj.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray())
                };
            case JsonArray array:
                var schema = new JsonObject { ["type"] = "array", ["minItems"] = Math.Min(array.Count, 1), ["maxItems"] = Math.Max(array.Count, 5) };
                if (array.Count > 0)
                    schema["items"] = InferSchema(array.FirstOrDefault(i => i is JsonObject) ?? array[0]);
                return schema;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        var text = value.GetValue<string>();
                        var format = Guid.TryParse(text, out _) ? "uuid"
                            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && text.Contains('T') ? "date-time"
                            : Regex.IsMatch(text, @"^\d{4}-\d\d-\d\d$") ? "date"
                            : Regex.IsMatch(text, @"^[^@\s]+@[^@\s]+\.\w+$") ? "email"
                            : Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") ? "uri"
                            : null;
                        var s = new JsonObject { ["type"] = "string" };
                        if (format is not null)
                            s["format"] = format;
                        return s;
                    case JsonValueKind.Number:
                        return new JsonObject { ["type"] = value.ToJsonString().Contains('.') ? "number" : "integer" };
                    case JsonValueKind.True or JsonValueKind.False:
                        return new JsonObject { ["type"] = "boolean" };
                }
                break;
        }
        return new JsonObject { ["type"] = "null" };
    }

    /// <summary>Copies a schema with local <c>$ref</c>s replaced by their targets (recursive references are cut off).</summary>
    public static JsonNode? Inline(JsonNode root, JsonNode? schema, int depth = 0)
    {
        if (depth > MaxDepth)
            return new JsonObject();
        switch (schema)
        {
            case JsonObject obj when obj["$ref"] is JsonValue r && r.TryGetValue<string>(out var reference):
                return Inline(root, Interop.OpenApi.Pointer(root, reference), depth + 1);
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (k, v) in obj)
                    copy[k] = k is "example" or "examples" or "enum" or "const" or "default" ? v?.DeepClone() : Inline(root, v, depth + 1);
                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(n => Inline(root, n, depth + 1)).ToArray());
            default:
                return schema?.DeepClone();
        }
    }
}
