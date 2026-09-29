using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Dispatch.Application.Formatting;

/// <summary>How a response body is interpreted for pretty-printing.</summary>
public enum BodyFormat
{
    Json,
    Xml,
    Html,
    JavaScript,
    Form,
    Text
}

public static class BodyFormatter
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        // Keep non-ASCII text (e.g. Armenian, Cyrillic) readable instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Pretty-prints the body according to its Content-Type (sniffing the body when the type is missing or generic).</summary>
    public static string Pretty(string body, string? contentType = null) =>
        Pretty(body, Detect(contentType, body));

    /// <summary>Pretty-prints the body as <paramref name="format"/>; returns it unchanged when it doesn't parse as that format.</summary>
    public static string Pretty(string body, BodyFormat format)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        return format switch
        {
            BodyFormat.Json or BodyFormat.JavaScript => TryFormatJson(body, out var json) ? json : body,
            BodyFormat.Xml => TryFormatXml(body, out var xml) ? xml : body,
            BodyFormat.Html => FormatHtml(body),
            BodyFormat.Form => FormatForm(body),
            _ => body
        };
    }

    /// <summary>Maps a media type (e.g. <c>application/problem+json</c>) to a <see cref="BodyFormat"/>.</summary>
    public static BodyFormat Detect(string? contentType, string? body = null)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;

        if (type.Contains("json"))
            return BodyFormat.Json;
        if (type.Contains("html"))
            return BodyFormat.Html;
        if (type.Contains("xml"))
            return BodyFormat.Xml;
        if (type.Contains("javascript") || type.Contains("ecmascript"))
            return BodyFormat.JavaScript;
        if (type.Contains("x-www-form-urlencoded"))
            return BodyFormat.Form;
        if (type is "" or "text/plain" or "application/octet-stream")
            return Sniff(body);
        return BodyFormat.Text;
    }

    private static BodyFormat Sniff(string? body)
    {
        var trimmed = body?.TrimStart() ?? string.Empty;
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return TryFormatJson(trimmed, out _) ? BodyFormat.Json : BodyFormat.Text;
        if (trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return BodyFormat.Html;
        if (trimmed.StartsWith('<'))
            return TryFormatXml(trimmed, out _) ? BodyFormat.Xml : BodyFormat.Text;
        return BodyFormat.Text;
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

    /// <summary>Decodes <c>a=1&amp;b=x%20y</c> into one <c>key: value</c> line per field.</summary>
    public static string FormatForm(string body)
    {
        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        return string.Join(Environment.NewLine, body.Trim()
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                return eq < 0 ? Decode(pair) : $"{Decode(pair[..eq])}: {Decode(pair[(eq + 1)..])}";
            }));
    }

    // ---- HTML ------------------------------------------------------------------------------------

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"
    };

    // Elements whose content is not HTML and must be kept as written.
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "pre", "textarea"
    };

    // Elements whose end tag is optional: a sibling of the same name implicitly closes the previous one.
    private static readonly HashSet<string> SelfSiblingClosingElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "li", "p", "option", "tr", "td", "th", "dt", "dd"
    };

    private enum HtmlTokenKind { Open, Close, SelfClosing, Other, Text, Raw }

    private readonly record struct HtmlToken(HtmlTokenKind Kind, string Text, string Name = "");

    /// <summary>
    /// Re-indents HTML one tag per line. Tolerant of real-world markup: unclosed elements (e.g. &lt;p&gt;, &lt;li&gt;)
    /// don't skew indentation, and script/style/pre/textarea content is left untouched.
    /// </summary>
    public static string FormatHtml(string html)
    {
        var tokens = TokenizeHtml(html);
        var sb = new StringBuilder();
        var open = new List<string>();

        void Line(string text)
        {
            if (sb.Length > 0)
                sb.Append(Environment.NewLine);
            sb.Append(' ', open.Count * 2).Append(text);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token.Kind)
            {
                case HtmlTokenKind.Open:
                    if (open.Count > 0 && SelfSiblingClosingElements.Contains(token.Name)
                        && string.Equals(open[^1], token.Name, StringComparison.OrdinalIgnoreCase))
                        open.RemoveAt(open.Count - 1);

                    // Keep short leaf elements on one line: <title>Home</title>, <td></td>.
                    if (i + 1 < tokens.Count && IsCloseOf(tokens[i + 1], token.Name))
                    {
                        Line(token.Text + tokens[i + 1].Text);
                        i++;
                    }
                    else if (i + 2 < tokens.Count && tokens[i + 1].Kind is HtmlTokenKind.Text or HtmlTokenKind.Raw
                             && IsCloseOf(tokens[i + 2], token.Name)
                             && (tokens[i + 1].Kind == HtmlTokenKind.Text || !tokens[i + 1].Text.Contains('\n')))
                    {
                        Line(token.Text + tokens[i + 1].Text + tokens[i + 2].Text);
                        i += 2;
                    }
                    else
                    {
                        Line(token.Text);
                        open.Add(token.Name);
                    }
                    break;

                case HtmlTokenKind.Close:
                    var index = open.FindLastIndex(n => string.Equals(n, token.Name, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                        open.RemoveRange(index, open.Count - index);
                    Line(token.Text);
                    break;

                case HtmlTokenKind.Raw:
                    // Raw content keeps its own line structure; only surrounding blank lines are dropped.
                    if (sb.Length > 0)
                        sb.Append(Environment.NewLine);
                    sb.Append(token.Text.Trim('\r', '\n'));
                    break;

                default:
                    Line(token.Text);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool IsCloseOf(HtmlToken token, string name) =>
        token.Kind == HtmlTokenKind.Close && string.Equals(token.Name, name, StringComparison.OrdinalIgnoreCase);

    private static List<HtmlToken> TokenizeHtml(string html)
    {
        var tokens = new List<HtmlToken>();
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                var next = html.IndexOf('<', i);
                if (next < 0)
                    next = html.Length;
                AddText(tokens, html[i..next]);
                i = next;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? html.Length : end + 3;
                tokens.Add(new HtmlToken(HtmlTokenKind.Other, html[i..end]));
                i = end;
                continue;
            }

            var tagEnd = FindTagEnd(html, i);
            if (tagEnd < 0)
            {
                // A stray '<' (e.g. "a < b") is just text.
                AddText(tokens, html[i..]);
                break;
            }

            var tag = html[i..(tagEnd + 1)];
            var isClose = tag.StartsWith("</", StringComparison.Ordinal);
            var name = TagName(tag, isClose ? 2 : 1);
            i = tagEnd + 1;

            if (name.Length == 0)
                tokens.Add(new HtmlToken(HtmlTokenKind.Other, tag)); // <!DOCTYPE>, <?xml ?>
            else if (isClose)
                tokens.Add(new HtmlToken(HtmlTokenKind.Close, tag, name));
            else if (tag.EndsWith("/>", StringComparison.Ordinal) || VoidElements.Contains(name))
                tokens.Add(new HtmlToken(HtmlTokenKind.SelfClosing, tag, name));
            else
            {
                tokens.Add(new HtmlToken(HtmlTokenKind.Open, tag, name));
                if (RawTextElements.Contains(name))
                {
                    var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                    if (close < 0)
                        close = html.Length;
                    if (!string.IsNullOrWhiteSpace(html[i..close]))
                        tokens.Add(new HtmlToken(HtmlTokenKind.Raw, html[i..close]));
                    i = close;
                }
            }
        }
        return tokens;
    }

    private static void AddText(List<HtmlToken> tokens, string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > 0)
            tokens.Add(new HtmlToken(HtmlTokenKind.Text, collapsed));
    }

    /// <summary>Index of the '&gt;' closing the tag at <paramref name="start"/>, skipping quoted attribute values.</summary>
    private static int FindTagEnd(string html, int start)
    {
        if (start + 1 >= html.Length || !(char.IsLetter(html[start + 1]) || html[start + 1] is '/' or '!' or '?'))
            return -1;

        char? quote = null;
        for (var j = start + 1; j < html.Length; j++)
        {
            var c = html[j];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '>')
                return j;
        }
        return -1;
    }

    private static string TagName(string tag, int offset)
    {
        var end = offset;
        while (end < tag.Length && (char.IsLetterOrDigit(tag[end]) || tag[end] is '-' or ':' or '_'))
            end++;
        return tag[offset..end];
    }
}
