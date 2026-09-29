using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Dispatch.Application.Formatting;

public static class BodyFormatter
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        // Keep non-ASCII text (e.g. Armenian, Cyrillic) readable instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Pretty-prints JSON or XML; returns the input unchanged when it is neither.</summary>
    public static string Pretty(string body, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        if (TryFormatJson(body, out var json))
            return json;

        var looksXml = contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true
                       || body.TrimStart().StartsWith('<');
        return looksXml && TryFormatXml(body, out var xml) ? xml : body;
    }

    public static bool TryFormatJson(string text, out string formatted)
    {
        formatted = text;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            formatted = node?.ToJsonString(PrettyJson) ?? text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryFormatXml(string text, out string formatted)
    {
        formatted = text;
        try
        {
            var doc = XDocument.Parse(text);
            formatted = (doc.Declaration is null ? string.Empty : doc.Declaration + Environment.NewLine) + doc;
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
