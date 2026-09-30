using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dispatch.Infrastructure.Interop;

/// <summary>Converts YAML documents to JSON nodes, inferring scalar types the way YAML 1.2 core schema does.</summary>
public static class Yaml
{
    public static JsonNode? ToJson(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Invalid YAML (line {ex.Start.Line}): {ex.Message}");
        }
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode, 0);
    }

    private static JsonNode? Convert(YamlNode node, int depth)
    {
        if (depth > 200)
            throw new FormatException("YAML nesting is too deep.");
        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                    obj[((YamlScalarNode)key).Value ?? ""] = Convert(value, depth + 1);
                return obj;
            case YamlSequenceNode sequence:
                return new JsonArray(sequence.Children.Select(c => Convert(c, depth + 1)).ToArray());
            case YamlScalarNode scalar:
                var text = scalar.Value ?? "";
                if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
                    return JsonValue.Create(text);
                return text switch
                {
                    "" or "~" or "null" or "Null" or "NULL" => null,
                    "true" or "True" or "TRUE" => JsonValue.Create(true),
                    "false" or "False" or "FALSE" => JsonValue.Create(false),
                    _ when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
                    _ when text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                           && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) => JsonValue.Create(hex),
                    _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !text.EndsWith('.') => JsonValue.Create(d),
                    _ => JsonValue.Create(text)
                };
            default:
                return null; // aliases are resolved by YamlStream
        }
    }
}
