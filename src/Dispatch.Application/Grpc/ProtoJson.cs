using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dispatch.Application.Grpc;

/// <summary>
/// Converts between protobuf binary messages and the proto3 JSON mapping, driven by a <see cref="ProtoSchema"/>
/// (no generated code). Well-known types (Timestamp, Duration, wrappers, Struct/Value/ListValue, Empty, FieldMask,
/// Any) use their special JSON forms.
/// </summary>
public sealed class ProtoJson(ProtoSchema schema)
{
    private const int MaxDepth = 100;

    /// <summary>When decoding, include fields that are unset (0, "", false, [], {}), which is easier to read in a test tool.</summary>
    public bool EmitDefaults { get; init; } = true;

    // ---- JSON -> binary ------------------------------------------------------------------------------

    public byte[] Encode(string messageType, string json)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Request message is not valid JSON: {ex.Message}");
        }
        return Encode(messageType, node);
    }

    public byte[] Encode(string messageType, JsonNode? node)
    {
        var writer = new ProtoWriter();
        WriteMessage(writer, schema.Message(messageType), node, "$", 0);
        return writer.ToArray();
    }

    private void WriteMessage(ProtoWriter writer, MessageDesc message, JsonNode? node, string path, int depth,
        bool allowSpecialJson = true)
    {
        if (depth > MaxDepth)
            throw new FormatException("Message nesting is too deep.");

        if (allowSpecialJson)
        {
            switch (message.FullName)
            {
                case "google.protobuf.Struct" when node is JsonObject structObject:
                    WriteStructFields(writer, structObject, path, depth);
                    return;
                case "google.protobuf.Value":
                    WriteValueFields(writer, node, path, depth);
                    return;
                case "google.protobuf.ListValue" when node is JsonArray list:
                    WriteListFields(writer, list, path, depth);
                    return;
                case "google.protobuf.Any" when node is JsonObject anyObject && anyObject.ContainsKey("@type"):
                    WriteAny(writer, anyObject, path, depth);
                    return;
            }
            if (WellKnownToProto(message.FullName, node, path) is { } plain)
            {
                WriteMessage(writer, message, plain, path, depth + 1, allowSpecialJson: false);
                return;
            }
        }

        if (node is null)
            return;
        if (node is not JsonObject obj)
            throw new FormatException($"{path}: expected an object for {message.FullName}");

        foreach (var (key, value) in obj)
        {
            var field = message.Fields.FirstOrDefault(f => f.EffectiveJsonName == key || f.Name == key)
                        ?? throw new FormatException($"{path}: {message.FullName} has no field '{key}'. " +
                                                     $"Fields: {string.Join(", ", message.Fields.Select(f => f.EffectiveJsonName))}");
            var fieldPath = $"{path}.{key}";
            if (value is null && !(field.TypeName == "google.protobuf.Value"))
                continue; // null means "not set"

            if (field.IsRepeated && IsMap(field, out var entry))
            {
                if (value is not JsonObject map)
                    throw new FormatException($"{fieldPath}: expected an object (map)");
                foreach (var (mapKey, mapValue) in map)
                {
                    var entryWriter = new ProtoWriter();
                    var keyField = entry.Fields.First(f => f.Number == 1);
                    var valueField = entry.Fields.First(f => f.Number == 2);
                    WriteSingle(entryWriter, keyField, MapKeyNode(keyField, mapKey, fieldPath), fieldPath, depth);
                    if (mapValue is not null || valueField.TypeName == "google.protobuf.Value")
                        WriteSingle(entryWriter, valueField, mapValue, $"{fieldPath}[{mapKey}]", depth);
                    writer.WriteTag(field.Number, WireType.LengthDelimited);
                    writer.WriteBytes(entryWriter.ToArray());
                }
            }
            else if (field.IsRepeated)
            {
                if (value is not JsonArray array)
                    throw new FormatException($"{fieldPath}: expected an array");
                if (IsPackable(field) && (field.Packed ?? IsProto3Field(message)))
                {
                    var packed = new ProtoWriter();
                    for (var i = 0; i < array.Count; i++)
                        WriteScalarValue(packed, field, array[i], $"{fieldPath}[{i}]");
                    writer.WriteTag(field.Number, WireType.LengthDelimited);
                    writer.WriteBytes(packed.ToArray());
                }
                else
                {
                    for (var i = 0; i < array.Count; i++)
                        WriteSingle(writer, field, array[i], $"{fieldPath}[{i}]", depth);
                }
            }
            else
            {
                WriteSingle(writer, field, value, fieldPath, depth);
            }
        }
    }

    private bool IsProto3Field(MessageDesc message) => schema.IsProto3(message);

    private void WriteSingle(ProtoWriter writer, FieldDesc field, JsonNode? value, string path, int depth)
    {
        switch (field.Type)
        {
            case ProtoType.Message:
                var nested = new ProtoWriter();
                WriteMessage(nested, schema.Message(field.TypeName!), value, path, depth + 1);
                writer.WriteTag(field.Number, WireType.LengthDelimited);
                writer.WriteBytes(nested.ToArray());
                break;
            case ProtoType.Group:
                writer.WriteTag(field.Number, WireType.StartGroup);
                WriteMessage(writer, schema.Message(field.TypeName!), value, path, depth + 1);
                writer.WriteTag(field.Number, WireType.EndGroup);
                break;
            case ProtoType.String:
                writer.WriteTag(field.Number, WireType.LengthDelimited);
                writer.WriteString(AsString(value, path));
                break;
            case ProtoType.Bytes:
                writer.WriteTag(field.Number, WireType.LengthDelimited);
                writer.WriteBytes(DecodeBase64(AsString(value, path), path));
                break;
            default:
                writer.WriteTag(field.Number, WireTypeOf(field.Type));
                WriteScalarValue(writer, field, value, path);
                break;
        }
    }

    private void WriteScalarValue(ProtoWriter writer, FieldDesc field, JsonNode? value, string path)
    {
        switch (field.Type)
        {
            case ProtoType.Double:
                writer.WriteFixed64(BitConverter.DoubleToUInt64Bits(AsDouble(value, path)));
                break;
            case ProtoType.Float:
                writer.WriteFixed32(BitConverter.SingleToUInt32Bits((float)AsDouble(value, path)));
                break;
            case ProtoType.Int64:
                writer.WriteVarint((ulong)AsLong(value, path));
                break;
            case ProtoType.UInt64:
                writer.WriteVarint(AsULong(value, path));
                break;
            case ProtoType.Int32:
                writer.WriteVarint((ulong)(long)checked((int)AsLong(value, path)));
                break;
            case ProtoType.UInt32:
                writer.WriteVarint(checked((uint)AsULong(value, path)));
                break;
            case ProtoType.SInt32:
                writer.WriteVarint(ProtoWriter.ZigZag32(checked((int)AsLong(value, path))));
                break;
            case ProtoType.SInt64:
                writer.WriteVarint(ProtoWriter.ZigZag64(AsLong(value, path)));
                break;
            case ProtoType.Fixed32:
                writer.WriteFixed32(checked((uint)AsULong(value, path)));
                break;
            case ProtoType.SFixed32:
                writer.WriteFixed32((uint)checked((int)AsLong(value, path)));
                break;
            case ProtoType.Fixed64:
                writer.WriteFixed64(AsULong(value, path));
                break;
            case ProtoType.SFixed64:
                writer.WriteFixed64((ulong)AsLong(value, path));
                break;
            case ProtoType.Bool:
                writer.WriteVarint(AsBool(value, path) ? 1UL : 0UL);
                break;
            case ProtoType.Enum:
                writer.WriteVarint((ulong)(long)EnumNumber(field, value, path));
                break;
            default:
                throw new FormatException($"{path}: cannot write {field.Type} as a scalar");
        }
    }

    private int EnumNumber(FieldDesc field, JsonNode? value, string path)
    {
        var e = schema.Enum(field.TypeName);
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
            return (int)AsLong(value, path);
        var name = AsString(value, path);
        if (e is not null)
            foreach (var (n, number) in e.Values)
                if (n == name)
                    return number;
        if (int.TryParse(name, out var numeric))
            return numeric;
        throw new FormatException($"{path}: '{name}' is not a value of {field.TypeName}" +
                                  (e is null ? "" : $" ({string.Join(", ", e.Values.Select(x => x.Name))})"));
    }

    private void WriteAny(ProtoWriter writer, JsonObject any, string path, int depth)
    {
        var typeUrl = any["@type"]?.GetValue<string>() ?? throw new FormatException($"{path}: Any needs @type");
        var typeName = typeUrl[(typeUrl.LastIndexOf('/') + 1)..];
        JsonNode? content;
        if (IsWellKnownWithSpecialJson(typeName))
            content = any["value"];
        else
            content = new JsonObject(any.Where(kv => kv.Key != "@type").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));

        var inner = new ProtoWriter();
        WriteMessage(inner, schema.Message(typeName), content, path, depth + 1);
        writer.WriteTag(1, WireType.LengthDelimited);
        writer.WriteString(typeUrl);
        writer.WriteTag(2, WireType.LengthDelimited);
        writer.WriteBytes(inner.ToArray());
    }

    /// <summary>Converts the special JSON form of a well-known type to its plain message form, or null if not a WKT.</summary>
    private static JsonNode? WellKnownToProto(string typeName, JsonNode? node, string path)
    {
        switch (typeName)
        {
            case "google.protobuf.Timestamp" when node is JsonValue:
                var text = node.GetValue<string>();
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                    throw new FormatException($"{path}: '{text}' is not an RFC 3339 timestamp");
                var ticks = ts.UtcTicks - DateTimeOffset.UnixEpoch.Ticks;
                var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var remainder);
                if (remainder < 0)
                {
                    seconds--;
                    remainder += TimeSpan.TicksPerSecond;
                }
                return new JsonObject { ["seconds"] = seconds.ToString(CultureInfo.InvariantCulture), ["nanos"] = (int)(remainder * 100) };
            case "google.protobuf.Duration" when node is JsonValue:
                var d = node.GetValue<string>().Trim();
                if (!d.EndsWith('s') || !decimal.TryParse(d[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
                    throw new FormatException($"{path}: '{d}' is not a duration like \"1.5s\"");
                var wholeSeconds = decimal.Truncate(total);
                return new JsonObject
                {
                    ["seconds"] = ((long)wholeSeconds).ToString(CultureInfo.InvariantCulture),
                    ["nanos"] = (int)((total - wholeSeconds) * 1_000_000_000m)
                };
            case "google.protobuf.DoubleValue" or "google.protobuf.FloatValue" or "google.protobuf.Int64Value"
                or "google.protobuf.UInt64Value" or "google.protobuf.Int32Value" or "google.protobuf.UInt32Value"
                or "google.protobuf.BoolValue" or "google.protobuf.StringValue" or "google.protobuf.BytesValue"
                when node is not JsonObject:
                return new JsonObject { ["value"] = node?.DeepClone() };
            case "google.protobuf.FieldMask" when node is JsonValue:
                return new JsonObject
                {
                    ["paths"] = new JsonArray(node.GetValue<string>().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => (JsonNode?)JsonValue.Create(ToSnakeCase(p))).ToArray())
                };
            default:
                return null;
        }
    }

    // google.protobuf.Struct / Value / ListValue map to arbitrary JSON; they are encoded directly.

    private void WriteStructFields(ProtoWriter writer, JsonObject obj, string path, int depth)
    {
        foreach (var (key, value) in obj)
        {
            var entry = new ProtoWriter();
            entry.WriteTag(1, WireType.LengthDelimited);
            entry.WriteString(key);
            var valueWriter = new ProtoWriter();
            WriteValueFields(valueWriter, value, $"{path}.{key}", depth + 1);
            entry.WriteTag(2, WireType.LengthDelimited);
            entry.WriteBytes(valueWriter.ToArray());
            writer.WriteTag(1, WireType.LengthDelimited);
            writer.WriteBytes(entry.ToArray());
        }
    }

    private void WriteListFields(ProtoWriter writer, JsonArray list, string path, int depth)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var valueWriter = new ProtoWriter();
            WriteValueFields(valueWriter, list[i], $"{path}[{i}]", depth + 1);
            writer.WriteTag(1, WireType.LengthDelimited);
            writer.WriteBytes(valueWriter.ToArray());
        }
    }

    private void WriteValueFields(ProtoWriter writer, JsonNode? node, string path, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("Value nesting is too deep.");
        switch (node)
        {
            case null:
                writer.WriteTag(1, WireType.Varint);
                writer.WriteVarint(0);
                break;
            case JsonObject obj:
                var structWriter = new ProtoWriter();
                WriteStructFields(structWriter, obj, path, depth + 1);
                writer.WriteTag(5, WireType.LengthDelimited);
                writer.WriteBytes(structWriter.ToArray());
                break;
            case JsonArray array:
                var listWriter = new ProtoWriter();
                WriteListFields(listWriter, array, path, depth + 1);
                writer.WriteTag(6, WireType.LengthDelimited);
                writer.WriteBytes(listWriter.ToArray());
                break;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        writer.WriteTag(3, WireType.LengthDelimited);
                        writer.WriteString(v.GetValue<string>());
                        break;
                    case JsonValueKind.Number:
                        writer.WriteTag(2, WireType.Fixed64);
                        writer.WriteFixed64(BitConverter.DoubleToUInt64Bits(double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture)));
                        break;
                    case JsonValueKind.True or JsonValueKind.False:
                        writer.WriteTag(4, WireType.Varint);
                        writer.WriteVarint(v.GetValueKind() == JsonValueKind.True ? 1UL : 0UL);
                        break;
                    default:
                        writer.WriteTag(1, WireType.Varint);
                        writer.WriteVarint(0);
                        break;
                }
                break;
        }
    }

    // ---- binary -> JSON ------------------------------------------------------------------------------

    public string DecodeToJson(string messageType, ReadOnlySpan<byte> data, bool indented = true) =>
        Decode(messageType, data)?.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) ?? "null";

    public JsonNode? Decode(string messageType, ReadOnlySpan<byte> data) => ReadMessage(schema.Message(messageType), data, 0);

    private JsonNode? ReadMessage(MessageDesc message, ReadOnlySpan<byte> data, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("Message nesting is too deep.");

        var result = new JsonObject();
        var repeated = new Dictionary<int, JsonArray>();
        var maps = new Dictionary<int, JsonObject>();
        var reader = new ProtoReader(data);

        while (!reader.End)
        {
            var (number, wire) = reader.ReadTag();
            var field = message.FieldByNumber(number);
            if (field is null)
            {
                reader.Skip(wire, number);
                continue;
            }

            if (field.IsRepeated && IsMap(field, out var entry))
            {
                var entryNode = ReadMessage(entry, reader.ReadLengthDelimited(), depth + 1) as JsonObject;
                var keyField = entry.Fields.First(f => f.Number == 1);
                var valueField = entry.Fields.First(f => f.Number == 2);
                var key = entryNode?[keyField.EffectiveJsonName] is { } k ? JsonToMapKey(k) : DefaultMapKey(keyField);
                var value = entryNode?[valueField.EffectiveJsonName]?.DeepClone() ?? DefaultValue(valueField);
                if (!maps.TryGetValue(number, out var map))
                    maps[number] = map = new JsonObject();
                map[key] = value;
                continue;
            }

            if (field.IsRepeated)
            {
                if (!repeated.TryGetValue(number, out var array))
                    repeated[number] = array = new JsonArray();
                if (wire == WireType.LengthDelimited && IsPackable(field))
                {
                    var packed = new ProtoReader(reader.ReadLengthDelimited());
                    while (!packed.End)
                        array.Add(ReadScalar(ref packed, field));
                }
                else
                {
                    array.Add(ReadSingle(ref reader, field, wire, depth));
                }
                continue;
            }

            result[field.EffectiveJsonName] = ReadSingle(ref reader, field, wire, depth);
        }

        foreach (var field in message.Fields)
        {
            var name = field.EffectiveJsonName;
            if (maps.TryGetValue(field.Number, out var map))
                result[name] = map;
            else if (repeated.TryGetValue(field.Number, out var array))
                result[name] = array;
            else if (EmitDefaults && !result.ContainsKey(name) && field.OneofIndex is null && !field.IsMessage)
                result[name] = field.IsRepeated ? (IsMap(field, out _) ? new JsonObject() : new JsonArray()) : DefaultValue(field);
            else if (EmitDefaults && !result.ContainsKey(name) && field.IsRepeated)
                result[name] = IsMap(field, out _) ? new JsonObject() : new JsonArray();
        }

        // Keep declaration order for readability.
        var ordered = new JsonObject();
        foreach (var field in message.Fields)
            if (result.ContainsKey(field.EffectiveJsonName))
                ordered[field.EffectiveJsonName] = result[field.EffectiveJsonName]?.DeepClone();

        return WellKnownToJson(message.FullName, ordered);
    }

    private JsonNode? ReadSingle(ref ProtoReader reader, FieldDesc field, WireType wire, int depth)
    {
        switch (field.Type)
        {
            case ProtoType.Message:
                var bytes = reader.ReadLengthDelimited();
                if (field.TypeName == "google.protobuf.Any")
                    return ReadAny(bytes, depth);
                return ReadMessage(schema.Message(field.TypeName!), bytes, depth + 1);
            case ProtoType.Group:
                return ReadMessage(schema.Message(field.TypeName!), reader.ReadGroup(field.Number), depth + 1);
            case ProtoType.String:
                return reader.ReadString();
            case ProtoType.Bytes:
                return Convert.ToBase64String(reader.ReadLengthDelimited());
            default:
                if (wire != WireTypeOf(field.Type))
                    throw new FormatException($"Field {field.Name} has wire type {wire}, expected {WireTypeOf(field.Type)}.");
                return ReadScalar(ref reader, field);
        }
    }

    private JsonNode? ReadScalar(ref ProtoReader reader, FieldDesc field) => field.Type switch
    {
        ProtoType.Double => Number(BitConverter.UInt64BitsToDouble(reader.ReadFixed64())),
        ProtoType.Float => Number(BitConverter.UInt32BitsToSingle(reader.ReadFixed32())),
        ProtoType.Int64 => ((long)reader.ReadVarint()).ToString(CultureInfo.InvariantCulture),
        ProtoType.UInt64 => reader.ReadVarint().ToString(CultureInfo.InvariantCulture),
        ProtoType.Int32 => (int)reader.ReadVarint(),
        ProtoType.UInt32 => (uint)reader.ReadVarint(),
        ProtoType.SInt32 => ProtoWriter.UnZigZag32((uint)reader.ReadVarint()),
        ProtoType.SInt64 => ProtoWriter.UnZigZag64(reader.ReadVarint()).ToString(CultureInfo.InvariantCulture),
        ProtoType.Fixed32 => reader.ReadFixed32(),
        ProtoType.SFixed32 => (int)reader.ReadFixed32(),
        ProtoType.Fixed64 => reader.ReadFixed64().ToString(CultureInfo.InvariantCulture),
        ProtoType.SFixed64 => ((long)reader.ReadFixed64()).ToString(CultureInfo.InvariantCulture),
        ProtoType.Bool => reader.ReadVarint() != 0,
        ProtoType.Enum => EnumName(field, (int)reader.ReadVarint()),
        _ => throw new FormatException($"Unexpected scalar type {field.Type}")
    };

    private JsonNode EnumName(FieldDesc field, int number)
    {
        if (field.TypeName == "google.protobuf.NullValue")
            return JsonValue.Create("NULL_VALUE")!;
        return schema.Enum(field.TypeName)?.NameOf(number) is { } name ? JsonValue.Create(name)! : JsonValue.Create(number);
    }

    private static JsonNode Number(double value) =>
        double.IsNaN(value) ? JsonValue.Create("NaN")! :
        double.IsPositiveInfinity(value) ? JsonValue.Create("Infinity")! :
        double.IsNegativeInfinity(value) ? JsonValue.Create("-Infinity")! :
        JsonValue.Create(value);

    private JsonNode? ReadAny(ReadOnlySpan<byte> data, int depth)
    {
        var reader = new ProtoReader(data);
        string typeUrl = "";
        ReadOnlySpan<byte> value = default;
        while (!reader.End)
        {
            var (number, wire) = reader.ReadTag();
            if (number == 1) typeUrl = reader.ReadString();
            else if (number == 2) value = reader.ReadLengthDelimited();
            else reader.Skip(wire, number);
        }

        var typeName = typeUrl[(typeUrl.LastIndexOf('/') + 1)..];
        if (!schema.Messages.ContainsKey(typeName))
            return new JsonObject { ["@type"] = typeUrl, ["value"] = Convert.ToBase64String(value) };

        var inner = ReadMessage(schema.Message(typeName), value, depth + 1);
        if (inner is JsonObject obj && !IsWellKnownWithSpecialJson(typeName))
        {
            var withType = new JsonObject { ["@type"] = typeUrl };
            foreach (var (k, v) in obj)
                withType[k] = v?.DeepClone();
            return withType;
        }
        return new JsonObject { ["@type"] = typeUrl, ["value"] = inner?.DeepClone() };
    }

    private static JsonNode? WellKnownToJson(string typeName, JsonObject plain)
    {
        switch (typeName)
        {
            case "google.protobuf.Timestamp":
            {
                var seconds = long.Parse(plain["seconds"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);
                var nanos = plain["nanos"]?.GetValue<int>() ?? 0;
                var ts = DateTimeOffset.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100);
                return ts.ToString(nanos == 0 ? "yyyy-MM-ddTHH:mm:ssZ" : "yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
            }
            case "google.protobuf.Duration":
            {
                var seconds = long.Parse(plain["seconds"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);
                var nanos = plain["nanos"]?.GetValue<int>() ?? 0;
                var total = seconds + nanos / 1_000_000_000m;
                return total.ToString("0.#########", CultureInfo.InvariantCulture) + "s";
            }
            case "google.protobuf.DoubleValue" or "google.protobuf.FloatValue" or "google.protobuf.Int64Value"
                or "google.protobuf.UInt64Value" or "google.protobuf.Int32Value" or "google.protobuf.UInt32Value"
                or "google.protobuf.BoolValue" or "google.protobuf.StringValue" or "google.protobuf.BytesValue":
                return plain["value"]?.DeepClone();
            case "google.protobuf.FieldMask":
                return string.Join(",", (plain["paths"] as JsonArray ?? []).Select(p => FieldDesc.ToJsonName(p?.GetValue<string>() ?? "")));
            case "google.protobuf.Struct":
                return FromStructFields(plain["fields"] as JsonObject);
            case "google.protobuf.ListValue":
                return new JsonArray((plain["values"] as JsonArray ?? []).Select(v => v?.DeepClone()).ToArray());
            case "google.protobuf.Value":
                return FromValue(plain);
            case "google.protobuf.Empty":
                return new JsonObject();
            default:
                return plain;
        }
    }

    // Struct fields and Value/ListValue members have already been converted to plain JSON when decoded,
    // because WellKnownToJson runs bottom-up.
    private static JsonNode FromStructFields(JsonObject? fields) =>
        new JsonObject((fields ?? []).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));

    private static JsonNode? FromValue(JsonObject plain)
    {
        foreach (var key in new[] { "structValue", "listValue", "stringValue", "numberValue", "boolValue" })
            if (plain.TryGetPropertyValue(key, out var v) && v is not null)
                return v.DeepClone();
        return null;
    }

    private static bool IsWellKnownWithSpecialJson(string typeName) => typeName is
        "google.protobuf.Timestamp" or "google.protobuf.Duration" or "google.protobuf.FieldMask" or "google.protobuf.Struct"
        or "google.protobuf.Value" or "google.protobuf.ListValue" or "google.protobuf.DoubleValue" or "google.protobuf.FloatValue"
        or "google.protobuf.Int64Value" or "google.protobuf.UInt64Value" or "google.protobuf.Int32Value"
        or "google.protobuf.UInt32Value" or "google.protobuf.BoolValue" or "google.protobuf.StringValue"
        or "google.protobuf.BytesValue" or "google.protobuf.Empty";

    // ---- Templates -----------------------------------------------------------------------------------

    /// <summary>A JSON skeleton of a message with default values, for the request editor.</summary>
    public string Template(string messageType, bool indented = true)
    {
        var node = TemplateNode(schema.Message(messageType), 0, []);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }) ?? "{}";
    }

    private JsonNode? TemplateNode(MessageDesc message, int depth, HashSet<string> visiting)
    {
        switch (message.FullName)
        {
            case "google.protobuf.Timestamp": return DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            case "google.protobuf.Duration": return "1s";
            case "google.protobuf.Struct": return new JsonObject();
            case "google.protobuf.Value": return null;
            case "google.protobuf.ListValue": return new JsonArray();
            case "google.protobuf.FieldMask": return "";
            case "google.protobuf.Any": return new JsonObject { ["@type"] = "type.googleapis.com/" };
        }
        if (IsWellKnownWithSpecialJson(message.FullName) && message.Fields.Count == 1)
            return DefaultValue(message.Fields[0]);

        var obj = new JsonObject();
        if (depth > 4 || !visiting.Add(message.FullName))
            return obj;
        var seenOneofs = new HashSet<int>();
        foreach (var field in message.Fields)
        {
            // Only the first member of each oneof, so the template is valid as-is.
            if (field.OneofIndex is { } oneof && !field.Proto3Optional && !seenOneofs.Add(oneof))
                continue;
            JsonNode? value;
            if (field.IsRepeated && IsMap(field, out _))
                value = new JsonObject();
            else if (field.IsMessage)
            {
                var nested = TemplateNode(schema.Message(field.TypeName!), depth + 1, visiting);
                value = field.IsRepeated ? new JsonArray(nested) : nested;
            }
            else
                value = field.IsRepeated ? new JsonArray(DefaultValue(field)) : DefaultValue(field);
            obj[field.EffectiveJsonName] = value;
        }
        visiting.Remove(message.FullName);
        return obj;
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    private bool IsMap(FieldDesc field, out MessageDesc entry)
    {
        entry = null!;
        if (field.Type != ProtoType.Message || field.TypeName is null || !schema.Messages.TryGetValue(field.TypeName, out var m) || !m.IsMapEntry)
            return false;
        entry = m;
        return true;
    }

    private static bool IsPackable(FieldDesc field) =>
        field.Type is not (ProtoType.String or ProtoType.Bytes or ProtoType.Message or ProtoType.Group);

    private static WireType WireTypeOf(ProtoType type) => type switch
    {
        ProtoType.Double or ProtoType.Fixed64 or ProtoType.SFixed64 => WireType.Fixed64,
        ProtoType.Float or ProtoType.Fixed32 or ProtoType.SFixed32 => WireType.Fixed32,
        ProtoType.String or ProtoType.Bytes or ProtoType.Message => WireType.LengthDelimited,
        ProtoType.Group => WireType.StartGroup,
        _ => WireType.Varint
    };

    private JsonNode? DefaultValue(FieldDesc field) => field.Type switch
    {
        ProtoType.String => "",
        ProtoType.Bytes => "",
        ProtoType.Bool => false,
        ProtoType.Enum => field.TypeName == "google.protobuf.NullValue"
            ? null
            : schema.Enum(field.TypeName)?.Values.FirstOrDefault().Name is { Length: > 0 } first ? JsonValue.Create(first) : JsonValue.Create(0),
        ProtoType.Int64 or ProtoType.UInt64 or ProtoType.SInt64 or ProtoType.Fixed64 or ProtoType.SFixed64 => "0",
        ProtoType.Double or ProtoType.Float => 0.0,
        ProtoType.Message or ProtoType.Group => null,
        _ => 0
    };

    private static string DefaultMapKey(FieldDesc keyField) => keyField.Type switch
    {
        ProtoType.String => "",
        ProtoType.Bool => "false",
        _ => "0"
    };

    private static string JsonToMapKey(JsonNode key) => key is JsonValue v && v.TryGetValue<string>(out var s)
        ? s
        : key.ToJsonString();

    private static JsonNode? MapKeyNode(FieldDesc keyField, string key, string path) => keyField.Type switch
    {
        ProtoType.String => key,
        ProtoType.Bool => key switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FormatException($"{path}: map key '{key}' must be true or false")
        },
        _ => key
    };

    private static string AsString(JsonNode? value, string path) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new FormatException($"{path}: expected a string");

    private static double AsDouble(JsonNode? value, string path)
    {
        if (value is JsonValue v)
        {
            if (v.GetValueKind() == JsonValueKind.Number)
                return double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
            if (v.TryGetValue<string>(out var s))
            {
                return s switch
                {
                    "NaN" => double.NaN,
                    "Infinity" => double.PositiveInfinity,
                    "-Infinity" => double.NegativeInfinity,
                    _ => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? d
                        : throw new FormatException($"{path}: '{s}' is not a number")
                };
            }
        }
        throw new FormatException($"{path}: expected a number");
    }

    private static long AsLong(JsonNode? value, string path)
    {
        var text = NumberText(value, path);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == Math.Floor(d)
            && d is >= long.MinValue and <= long.MaxValue)
            return (long)d;
        throw new FormatException($"{path}: '{text}' is not an integer");
    }

    private static ulong AsULong(JsonNode? value, string path)
    {
        var text = NumberText(value, path);
        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
            return u;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == Math.Floor(d) && d >= 0)
            return (ulong)d;
        throw new FormatException($"{path}: '{text}' is not an unsigned integer");
    }

    private static string NumberText(JsonNode? value, string path) => value switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
        _ => throw new FormatException($"{path}: expected a number")
    };

    private static bool AsBool(JsonNode? value, string path) => value switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var b) => b,
        _ => throw new FormatException($"{path}: expected true or false")
    };

    private static byte[] DecodeBase64(string text, string path)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            throw new FormatException($"{path}: bytes must be base64");
        }
    }

    private static string ToSnakeCase(string camel)
    {
        var sb = new StringBuilder();
        foreach (var c in camel)
        {
            if (char.IsUpper(c))
                sb.Append('_').Append(char.ToLowerInvariant(c));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
