using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dispatch.Application.Formatting;

/// <summary>
/// A Handlebars-compatible subset for <c>pm.visualizer.set(template, data)</c>: <c>{{path}}</c> (HTML-escaped),
/// <c>{{{path}}}</c> (raw), <c>{{this}}</c>, <c>{{@index}}</c>, <c>{{@key}}</c>, <c>{{../parent}}</c>,
/// <c>{{#each}}</c>, <c>{{#if}}</c> / <c>{{else}}</c>, <c>{{#unless}}</c>, <c>{{#with}}</c> and <c>{{! comments }}</c>.
/// </summary>
public static partial class Template
{
    [GeneratedRegex(@"\{\{\{\s*(.+?)\s*\}\}\}|\{\{(~?)\s*([#/^!]?)\s*(.*?)\s*(~?)\}\}", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    private abstract record Node;
    private sealed record TextNode(string Text) : Node;
    private sealed record ValueNode(string Path, bool Raw) : Node;
    private sealed record BlockNode(string Helper, string Argument, List<Node> Body, List<Node> Else) : Node;

    private sealed record Frame(JsonNode? Value, Frame? Parent, int? Index, string? Key);

    public static string Render(string template, JsonNode? data)
    {
        var tokens = Tokenize(template);
        var position = 0;
        var (nodes, _) = ParseNodes(tokens, ref position, null);
        var sb = new StringBuilder();
        Emit(nodes, new Frame(data, null, null, null), sb);
        return sb.ToString();
    }

    /// <summary>Wraps rendered visualizer output in a standalone HTML page.</summary>
    public static string Page(string title, string content) =>
        content.Contains("<html", StringComparison.OrdinalIgnoreCase)
            ? content
            : "<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title>\n" +
              "<style>body{font-family:system-ui,sans-serif;margin:16px;color:#1f2328}table{border-collapse:collapse}" +
              "th,td{border:1px solid #d0d7de;padding:4px 8px;text-align:left}th{background:#f6f8fa}</style>\n" +
              "</head><body>\n" + content + "\n</body></html>\n";

    private enum TokenKind { Text, Value, Raw, Open, Close, Else }

    private sealed record Token(TokenKind Kind, string Text, string Argument = "");

    private static List<Token> Tokenize(string template)
    {
        var tokens = new List<Token>();
        var position = 0;
        foreach (Match m in TagRegex().Matches(template))
        {
            if (m.Index > position)
                tokens.Add(new Token(TokenKind.Text, template[position..m.Index]));
            position = m.Index + m.Length;
            if (m.Groups[1].Success)
            {
                tokens.Add(new Token(TokenKind.Raw, m.Groups[1].Value.Trim()));
                continue;
            }
            var content = m.Groups[4].Value.Trim();
            switch (m.Groups[3].Value)
            {
                case "!":
                    break;
                case "#":
                    var space = content.IndexOf(' ');
                    tokens.Add(new Token(TokenKind.Open, space < 0 ? content : content[..space], space < 0 ? "this" : content[(space + 1)..].Trim()));
                    break;
                case "/":
                    tokens.Add(new Token(TokenKind.Close, content));
                    break;
                default:
                    tokens.Add(content == "else" ? new Token(TokenKind.Else, "") : new Token(TokenKind.Value, content));
                    break;
            }
        }
        if (position < template.Length)
            tokens.Add(new Token(TokenKind.Text, template[position..]));
        return tokens;
    }

    /// <summary>Parses until the closing tag of <paramref name="block"/> (or the end); returns the body and else-branch.</summary>
    private static (List<Node> Body, List<Node> Else) ParseNodes(List<Token> tokens, ref int position, string? block)
    {
        var body = new List<Node>();
        var otherwise = new List<Node>();
        var current = body;
        while (position < tokens.Count)
        {
            var token = tokens[position++];
            switch (token.Kind)
            {
                case TokenKind.Text:
                    current.Add(new TextNode(token.Text));
                    break;
                case TokenKind.Value:
                case TokenKind.Raw:
                    current.Add(new ValueNode(token.Text, token.Kind == TokenKind.Raw));
                    break;
                case TokenKind.Open:
                    var (inner, innerElse) = ParseNodes(tokens, ref position, token.Text);
                    current.Add(new BlockNode(token.Text, token.Argument, inner, innerElse));
                    break;
                case TokenKind.Else when block is not null:
                    current = otherwise;
                    break;
                case TokenKind.Close when token.Text == block:
                    return (body, otherwise);
            }
        }
        return (body, otherwise);
    }

    private static void Emit(List<Node> nodes, Frame frame, StringBuilder sb)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode t:
                    sb.Append(t.Text);
                    break;
                case ValueNode v:
                    var text = Text(Lookup(v.Path, frame));
                    sb.Append(v.Raw ? text : WebUtility.HtmlEncode(text));
                    break;
                case BlockNode b:
                    EmitBlock(b, frame, sb);
                    break;
            }
        }
    }

    private static void EmitBlock(BlockNode block, Frame frame, StringBuilder sb)
    {
        var value = Lookup(block.Argument, frame);
        switch (block.Helper)
        {
            case "each":
                var any = false;
                if (value is JsonArray array)
                {
                    for (var i = 0; i < array.Count; i++, any = true)
                        Emit(block.Body, new Frame(array[i], frame, i, null), sb);
                }
                else if (value is JsonObject obj)
                {
                    var i = 0;
                    foreach (var (key, item) in obj)
                    {
                        Emit(block.Body, new Frame(item, frame, i++, key), sb);
                        any = true;
                    }
                }
                if (!any)
                    Emit(block.Else, frame, sb);
                break;
            case "if":
                Emit(Truthy(value) ? block.Body : block.Else, frame, sb);
                break;
            case "unless":
                Emit(Truthy(value) ? block.Else : block.Body, frame, sb);
                break;
            case "with":
                if (Truthy(value))
                    Emit(block.Body, new Frame(value, frame, null, null), sb);
                else
                    Emit(block.Else, frame, sb);
                break;
            default:
                Emit(block.Body, frame, sb);
                break;
        }
    }

    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true
    };

    private static JsonNode? Lookup(string path, Frame frame)
    {
        path = path.Trim();
        while (path.StartsWith("../", StringComparison.Ordinal) && frame.Parent is not null)
        {
            frame = frame.Parent;
            path = path[3..];
        }
        switch (path)
        {
            case "this" or ".":
                return frame.Value;
            case "@index":
                return frame.Index is { } i ? JsonValue.Create(i) : null;
            case "@key":
                return frame.Key is { } k ? JsonValue.Create(k) : null;
            case "@first":
                return JsonValue.Create(frame.Index == 0);
        }
        if (path.StartsWith("this.", StringComparison.Ordinal))
            path = path[5..];

        // Look the first segment up in this frame, then in enclosing frames (Handlebars' helper scoping is simpler, but
        // this is friendlier for templates written against the root object).
        for (var f = frame; f is not null; f = f.Parent)
        {
            var node = Walk(f.Value, path);
            if (node is not null)
                return node;
        }
        return null;
    }

    private static JsonNode? Walk(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = part.Trim('[', ']');
            node = node switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < a.Count => a[i],
                JsonArray a when segment == "length" => JsonValue.Create(a.Count),
                _ => null
            };
            if (node is null)
                return null;
        }
        return node;
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString()
    };
}
