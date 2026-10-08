using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dispatch.Application.Testing;

/// <summary>
/// Validates JSON against a JSON Schema (draft-07 / 2019-09 / 2020-12 core keywords, plus OpenAPI 3.0's
/// <c>nullable</c>). Local <c>$ref</c>s (<c>#/definitions/...</c>, <c>#/components/schemas/...</c>) are resolved
/// against <see cref="RootDocument"/>, which defaults to the schema itself.
/// </summary>
public sealed class JsonSchemaValidator
{
    private const int MaxDepth = 64;

    /// <summary>Schema patterns are user input; a timeout keeps a pathological regex from hanging validation.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public JsonSchemaValidator(JsonNode schema, JsonNode? rootDocument = null)
    {
        Schema = schema;
        RootDocument = rootDocument ?? schema;
    }

    public JsonNode Schema { get; }
    public JsonNode RootDocument { get; }

    public static IReadOnlyList<string> Validate(string instanceJson, string schemaJson)
    {
        JsonNode? instance;
        try
        {
            instance = JsonNode.Parse(instanceJson);
        }
        catch (JsonException ex)
        {
            return [$"Response is not valid JSON: {ex.Message}"];
        }
        var schema = JsonNode.Parse(schemaJson) ?? throw new FormatException("Schema is empty.");
        return new JsonSchemaValidator(schema).Validate(instance);
    }

    public IReadOnlyList<string> Validate(JsonNode? instance)
    {
        var errors = new List<string>();
        Check(instance, Schema, "$", errors, 0);
        return errors;
    }

    private void Check(JsonNode? instance, JsonNode? schema, string path, List<string> errors, int depth)
    {
        if (depth > MaxDepth)
        {
            errors.Add($"{path}: schema nesting too deep (recursive $ref?)");
            return;
        }

        switch (schema)
        {
            case null:
                return;
            case JsonValue boolSchema when boolSchema.GetValueKind() is JsonValueKind.True:
                return;
            case JsonValue boolSchema when boolSchema.GetValueKind() is JsonValueKind.False:
                errors.Add($"{path}: no value is allowed here");
                return;
            case not JsonObject:
                return;
        }

        var s = schema.AsObject();

        if (s["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
        {
            var target = ResolveRef(reference);
            if (target is null)
                errors.Add($"{path}: cannot resolve $ref '{reference}'");
            else
                Check(instance, target, path, errors, depth + 1);
            // Draft-07: siblings of $ref are ignored. Later drafts apply them; supporting both is harmless here.
        }

        var nullable = s["nullable"] is JsonValue n && n.GetValueKind() == JsonValueKind.True;
        if (instance is null && nullable)
            return;

        if (s["type"] is { } typeNode)
        {
            var types = typeNode is JsonArray arr
                ? arr.Select(t => t?.GetValue<string>() ?? "").ToList()
                : [typeNode.GetValue<string>()];
            if (nullable)
                types.Add("null");
            if (!types.Any(t => IsType(instance, t)))
            {
                errors.Add($"{path}: expected {string.Join(" or ", types)} but got {TypeOf(instance)}");
                return;
            }
        }

        if (s["enum"] is JsonArray enumValues && !enumValues.Any(e => JsonNode.DeepEquals(e, instance)))
            errors.Add($"{path}: value {Show(instance)} is not one of {enumValues.ToJsonString()}");

        if (s.ContainsKey("const") && !JsonNode.DeepEquals(s["const"], instance))
            errors.Add($"{path}: expected constant {Show(s["const"])} but got {Show(instance)}");

        switch (instance)
        {
            case JsonObject obj:
                CheckObject(obj, s, path, errors, depth);
                break;
            case JsonArray array:
                CheckArray(array, s, path, errors, depth);
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                CheckString(value.GetValue<string>(), s, path, errors);
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                CheckNumber(double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture), s, path, errors);
                break;
        }

        if (s["allOf"] is JsonArray allOf)
            foreach (var sub in allOf)
                Check(instance, sub, path, errors, depth + 1);

        if (s["anyOf"] is JsonArray anyOf && !anyOf.Any(sub => Passes(instance, sub, path, depth)))
            errors.Add($"{path}: does not match any schema in anyOf");

        if (s["oneOf"] is JsonArray oneOf)
        {
            var matches = oneOf.Count(sub => Passes(instance, sub, path, depth));
            if (matches != 1)
                errors.Add($"{path}: must match exactly one schema in oneOf (matched {matches})");
        }

        if (s["not"] is { } not && Passes(instance, not, path, depth))
            errors.Add($"{path}: must not match the 'not' schema");

        if (s["if"] is { } ifSchema)
        {
            if (Passes(instance, ifSchema, path, depth))
                Check(instance, s["then"], path, errors, depth + 1);
            else
                Check(instance, s["else"], path, errors, depth + 1);
        }
    }

    private bool Passes(JsonNode? instance, JsonNode? schema, string path, int depth)
    {
        var errors = new List<string>();
        Check(instance, schema, path, errors, depth + 1);
        return errors.Count == 0;
    }

    private void CheckObject(JsonObject obj, JsonObject s, string path, List<string> errors, int depth)
    {
        if (s["required"] is JsonArray required)
            foreach (var name in required.Select(r => r?.GetValue<string>()).Where(r => r is not null))
                if (!obj.ContainsKey(name!))
                    errors.Add($"{path}: missing required property '{name}'");

        var properties = s["properties"] as JsonObject;
        var patternProperties = s["patternProperties"] as JsonObject;

        foreach (var (name, value) in obj)
        {
            var childPath = $"{path}.{name}";
            var matched = false;
            if (properties is not null && properties.TryGetPropertyValue(name, out var propSchema))
            {
                matched = true;
                Check(value, propSchema, childPath, errors, depth + 1);
            }
            if (patternProperties is not null)
            {
                foreach (var (pattern, patternSchema) in patternProperties)
                {
                    if (!Regex.IsMatch(name, pattern, RegexOptions.None, RegexTimeout))
                        continue;
                    matched = true;
                    Check(value, patternSchema, childPath, errors, depth + 1);
                }
            }
            if (matched)
                continue;

            switch (s["additionalProperties"])
            {
                case JsonValue ap when ap.GetValueKind() == JsonValueKind.False:
                    errors.Add($"{path}: unexpected property '{name}'");
                    break;
                case JsonObject apSchema:
                    Check(value, apSchema, childPath, errors, depth + 1);
                    break;
            }
        }

        if (Int(s, "minProperties") is { } minP && obj.Count < minP)
            errors.Add($"{path}: must have at least {minP} properties");
        if (Int(s, "maxProperties") is { } maxP && obj.Count > maxP)
            errors.Add($"{path}: must have at most {maxP} properties");

        if (s["dependentRequired"] is JsonObject dependent)
            foreach (var (name, needs) in dependent)
                if (obj.ContainsKey(name) && needs is JsonArray list)
                    foreach (var need in list.Select(x => x?.GetValue<string>()))
                        if (need is not null && !obj.ContainsKey(need))
                            errors.Add($"{path}: property '{name}' requires '{need}'");
    }

    private void CheckArray(JsonArray array, JsonObject s, string path, List<string> errors, int depth)
    {
        var prefixCount = 0;
        if (s["prefixItems"] is JsonArray prefix)
        {
            prefixCount = prefix.Count;
            for (var i = 0; i < Math.Min(prefix.Count, array.Count); i++)
                Check(array[i], prefix[i], $"{path}[{i}]", errors, depth + 1);
        }

        switch (s["items"])
        {
            case JsonArray tuple: // draft-07 tuple form
                for (var i = 0; i < Math.Min(tuple.Count, array.Count); i++)
                    Check(array[i], tuple[i], $"{path}[{i}]", errors, depth + 1);
                break;
            case { } itemSchema:
                for (var i = prefixCount; i < array.Count; i++)
                    Check(array[i], itemSchema, $"{path}[{i}]", errors, depth + 1);
                break;
        }

        if (Int(s, "minItems") is { } min && array.Count < min)
            errors.Add($"{path}: must have at least {min} items (has {array.Count})");
        if (Int(s, "maxItems") is { } max && array.Count > max)
            errors.Add($"{path}: must have at most {max} items (has {array.Count})");

        if (s["uniqueItems"] is JsonValue u && u.GetValueKind() == JsonValueKind.True)
        {
            for (var i = 0; i < array.Count; i++)
                for (var j = i + 1; j < array.Count; j++)
                    if (JsonNode.DeepEquals(array[i], array[j]))
                    {
                        errors.Add($"{path}: items {i} and {j} are equal but uniqueItems is set");
                        return;
                    }
        }

        if (s["contains"] is { } contains && !array.Any(item => Passes(item, contains, path, depth)))
            errors.Add($"{path}: no item matches 'contains'");
    }

    private static void CheckString(string value, JsonObject s, string path, List<string> errors)
    {
        var length = new StringInfo(value).LengthInTextElements;
        if (Int(s, "minLength") is { } min && length < min)
            errors.Add($"{path}: must be at least {min} characters");
        if (Int(s, "maxLength") is { } max && length > max)
            errors.Add($"{path}: must be at most {max} characters");
        if (s["pattern"] is JsonValue p && p.TryGetValue<string>(out var pattern) && !Regex.IsMatch(value, pattern, RegexOptions.None, RegexTimeout))
            errors.Add($"{path}: '{value}' does not match pattern {pattern}");
        if (s["format"] is JsonValue f && f.TryGetValue<string>(out var format) && !MatchesFormat(value, format))
            errors.Add($"{path}: '{value}' is not a valid {format}");
    }

    private static void CheckNumber(double value, JsonObject s, string path, List<string> errors)
    {
        var min = Number(s, "minimum");
        var max = Number(s, "maximum");

        // OpenAPI 3.0 / draft-04 use boolean exclusiveMinimum; later drafts use a number.
        var exMinBool = s["exclusiveMinimum"] is JsonValue emb && emb.GetValueKind() == JsonValueKind.True;
        var exMaxBool = s["exclusiveMaximum"] is JsonValue exb && exb.GetValueKind() == JsonValueKind.True;

        if (min is { } mn && (exMinBool ? value <= mn : value < mn))
            errors.Add($"{path}: {value} is less than {(exMinBool ? "or equal to " : "")}minimum {mn}");
        if (max is { } mx && (exMaxBool ? value >= mx : value > mx))
            errors.Add($"{path}: {value} is greater than {(exMaxBool ? "or equal to " : "")}maximum {mx}");
        if (Number(s, "exclusiveMinimum") is { } exMin && value <= exMin)
            errors.Add($"{path}: {value} must be greater than {exMin}");
        if (Number(s, "exclusiveMaximum") is { } exMax && value >= exMax)
            errors.Add($"{path}: {value} must be less than {exMax}");
        if (Number(s, "multipleOf") is { } multiple and > 0)
        {
            var q = value / multiple;
            if (Math.Abs(q - Math.Round(q)) > 1e-9)
                errors.Add($"{path}: {value} is not a multiple of {multiple}");
        }
    }

    private static bool MatchesFormat(string value, string format) => format switch
    {
        "date-time" => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                       && value.Contains('T', StringComparison.OrdinalIgnoreCase),
        "date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "time" => TimeOnly.TryParse(value.Split('+', '-', 'Z')[0], CultureInfo.InvariantCulture, out _),
        "email" => Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.None, RegexTimeout),
        "uuid" => Guid.TryParse(value, out _),
        "uri" or "url" => Uri.TryCreate(value, UriKind.Absolute, out _),
        "uri-reference" => Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _),
        "ipv4" => IPAddress.TryParse(value, out var v4) && v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
        "ipv6" => IPAddress.TryParse(value, out var v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
        "hostname" => Uri.CheckHostName(value) != UriHostNameType.Unknown,
        "byte" => IsBase64(value),
        _ => true // unknown formats are annotations only
    };

    private static bool IsBase64(string value)
    {
        Span<byte> buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _);
    }

    private JsonNode? ResolveRef(string reference)
    {
        if (!reference.StartsWith('#'))
            return null; // remote refs are not fetched
        JsonNode? node = RootDocument;
        foreach (var raw in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i < a.Count => a[i],
                _ => null
            };
            if (node is null)
                return null;
        }
        return node;
    }

    private static bool IsType(JsonNode? instance, string type) => type switch
    {
        "null" => instance is null,
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        "string" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => instance is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.Number
                     && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                     && Math.Abs(d - Math.Round(d)) < 1e-12,
        _ => true
    };

    public static string TypeOf(JsonNode? instance) => instance switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null"
        },
        _ => "unknown"
    };

    private static string Show(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 80 ? text[..77] + "..." : text;
    }

    private static int? Int(JsonObject s, string name) => Number(s, name) is { } d ? (int)d : null;

    private static double? Number(JsonObject s, string name) =>
        s[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture)
            : null;
}
