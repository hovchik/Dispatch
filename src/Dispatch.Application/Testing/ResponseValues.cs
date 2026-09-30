using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Dispatch.Domain;

namespace Dispatch.Application.Testing;

/// <summary>Reads values out of a response by source and path (JSONPath, XPath, header, regex, ...).</summary>
public static class ResponseValues
{
    /// <summary>
    /// The value(s) selected by <paramref name="source"/> / <paramref name="path"/>. Empty when nothing matches.
    /// Throws <see cref="FormatException"/> for an invalid path or unparseable body.
    /// </summary>
    public static IReadOnlyList<string> Read(ApiResponse response, ValueSource source, string path)
    {
        switch (source)
        {
            case ValueSource.Status:
                return [response.StatusCode.ToString(CultureInfo.InvariantCulture)];
            case ValueSource.ResponseTime:
                return [((long)response.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)];
            case ValueSource.Size:
                return [response.SizeBytes.ToString(CultureInfo.InvariantCulture)];
            case ValueSource.MessageCount:
                return [response.Messages.Count(m => m.Direction == MessageDirection.Received)
                    .ToString(CultureInfo.InvariantCulture)];
            case ValueSource.Body:
                return [response.Body];
            case ValueSource.Header:
                return response.Headers.Concat(response.Trailers)
                    .Where(h => string.Equals(h.Name, path.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(h => h.Value)
                    .ToList();
            case ValueSource.JsonPath:
                return JsonPath.Select(ParseJson(BodyForJson(response)), path).Select(JsonPath.ToText).ToList();
            case ValueSource.XPath:
                return SelectXPath(response.Body, path);
            case ValueSource.Regex:
                var match = Regex.Match(response.Body, path, RegexOptions.Multiline, TimeSpan.FromSeconds(2));
                if (!match.Success)
                    return [];
                // The first capture group if there is one, else the whole match.
                return [match.Groups.Count > 1 ? match.Groups[1].Value : match.Value];
            default:
                return [];
        }
    }

    /// <summary>Streaming responses are matched against their received messages as a JSON array when the body is empty.</summary>
    private static string BodyForJson(ApiResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Body))
            return response.Body;
        var received = response.Messages.Where(m => m.Direction == MessageDirection.Received).ToList();
        if (received.Count == 0)
            return response.Body;
        var array = new JsonArray();
        foreach (var m in received)
        {
            try
            {
                array.Add(JsonNode.Parse(m.Content));
            }
            catch (JsonException)
            {
                array.Add(JsonValue.Create(m.Content));
            }
        }
        return array.ToJsonString();
    }

    public static JsonNode? ParseJson(string body)
    {
        try
        {
            return JsonNode.Parse(body, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Response body is not valid JSON ({ex.Message}).");
        }
    }

    /// <summary>
    /// Evaluates an XPath. Namespace prefixes used in the expression are bound to the document's own declarations,
    /// so <c>//soap:Body/m:GetPriceResponse</c> works without extra configuration; <c>local-name()</c> also works.
    /// </summary>
    public static IReadOnlyList<string> SelectXPath(string body, string xpath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"Response body is not valid XML ({ex.Message}).");
        }

        var navigator = doc.CreateNavigator();
        var namespaces = new XmlNamespaceManager(navigator.NameTable);
        foreach (var attr in doc.Descendants().Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = attr.Name.Namespace == XNamespace.None ? "" : attr.Name.LocalName;
            if (prefix.Length > 0 && namespaces.LookupNamespace(prefix) is null)
                namespaces.AddNamespace(prefix, attr.Value);
        }

        object result;
        try
        {
            result = navigator.Evaluate(xpath, namespaces);
        }
        catch (XPathException ex)
        {
            throw new FormatException($"Invalid XPath: {ex.Message}");
        }

        return result switch
        {
            XPathNodeIterator nodes => nodes.Cast<XPathNavigator>().Select(n => n.Value).ToList(),
            double d => [d.ToString(CultureInfo.InvariantCulture)],
            bool b => [b ? "true" : "false"],
            string s => [s],
            _ => []
        };
    }
}
