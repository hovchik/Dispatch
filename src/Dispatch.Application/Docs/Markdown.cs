using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Dispatch.Application.Docs;

/// <summary>
/// CommonMark-flavoured Markdown to HTML for request and collection descriptions: headings, paragraphs, emphasis,
/// inline code, fenced code, block quotes, ordered / unordered (nested) lists, task lists, GFM tables, links, images and
/// rules. Raw HTML is escaped, so descriptions can't inject markup into generated docs.
/// </summary>
public static partial class Markdown
{
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";
        var lines = markdown.Replace("\r\n", "\n").Replace('\t', ' ').Split('\n');
        var sb = new StringBuilder();
        var i = 0;
        RenderBlocks(lines, ref i, lines.Length, sb);
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex TableDivider();

    private static void RenderBlocks(string[] lines, ref int i, int end, StringBuilder sb)
    {
        while (i < end)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < end && !lines[i].TrimStart().StartsWith(fence))
                    code.Add(lines[i++]);
                i++; // closing fence
                sb.Append("<pre><code").Append(language.Length > 0 ? $" class=\"language-{Encode(language)}\"" : "").Append('>')
                    .Append(Encode(string.Join("\n", code))).Append("</code></pre>\n");
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                var level = heading.Groups[1].Length;
                var text = heading.Groups[2].Value;
                sb.Append($"<h{level} id=\"{Slug(text)}\">").Append(Inline(text)).Append($"</h{level}>\n");
                i++;
                continue;
            }

            if (Rule().IsMatch(line))
            {
                sb.Append("<hr>\n");
                i++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quoted = new List<string>();
                while (i < end && lines[i].TrimStart().StartsWith('>'))
                {
                    var q = lines[i++].TrimStart()[1..];
                    quoted.Add(q.StartsWith(' ') ? q[1..] : q);
                }
                var inner = quoted.ToArray();
                var j = 0;
                sb.Append("<blockquote>\n");
                RenderBlocks(inner, ref j, inner.Length, sb);
                sb.Append("</blockquote>\n");
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < end && TableDivider().IsMatch(lines[i + 1]))
            {
                RenderTable(lines, ref i, end, sb);
                continue;
            }

            if (ListItem().IsMatch(line))
            {
                RenderList(lines, ref i, end, Indent(line), sb);
                continue;
            }

            // Paragraph: until a blank line or another block starts.
            var paragraph = new List<string>();
            while (i < end && lines[i].Trim().Length > 0 && !StartsBlock(lines, i, end))
                paragraph.Add(lines[i++].Trim());
            if (paragraph.Count == 0)
                paragraph.Add(lines[i++].Trim());
            sb.Append("<p>").Append(Inline(string.Join("\n", paragraph)).Replace("\n", " ")).Append("</p>\n");
        }
    }

    private static bool StartsBlock(string[] lines, int i, int end)
    {
        var t = lines[i].TrimStart();
        return t.StartsWith("```") || t.StartsWith("~~~") || t.StartsWith('>') || Heading().IsMatch(t) || Rule().IsMatch(lines[i])
               || ListItem().IsMatch(lines[i]) || (t.StartsWith('|') && i + 1 < end && TableDivider().IsMatch(lines[i + 1]));
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static void RenderList(string[] lines, ref int i, int end, int indent, StringBuilder sb)
    {
        var ordered = char.IsDigit(lines[i].TrimStart()[0]);
        sb.Append(ordered ? "<ol>\n" : "<ul>\n");
        while (i < end)
        {
            var match = ListItem().Match(lines[i]);
            if (!match.Success || Indent(lines[i]) != indent || char.IsDigit(match.Groups[2].Value[0]) != ordered)
                break;
            var text = match.Groups[3].Value;
            var task = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
            sb.Append("<li>");
            if (task.Success)
                sb.Append(task.Groups[1].Value == " " ? "<input type=\"checkbox\" disabled> " : "<input type=\"checkbox\" checked disabled> ")
                    .Append(Inline(task.Groups[2].Value));
            else
                sb.Append(Inline(text));
            i++;

            // Continuation lines and nested lists (indented deeper than this item).
            while (i < end && lines[i].Trim().Length > 0 && Indent(lines[i]) > indent)
            {
                if (ListItem().IsMatch(lines[i]))
                {
                    sb.Append('\n');
                    RenderList(lines, ref i, end, Indent(lines[i]), sb);
                }
                else
                {
                    sb.Append(' ').Append(Inline(lines[i].Trim()));
                    i++;
                }
            }
            sb.Append("</li>\n");
            // A single blank line between items keeps the list going.
            if (i + 1 < end && lines[i].Trim().Length == 0 && ListItem().Match(lines[i + 1]) is { Success: true } next && Indent(lines[i + 1]) == indent
                && char.IsDigit(next.Groups[2].Value[0]) == ordered)
                i++;
        }
        sb.Append(ordered ? "</ol>\n" : "</ul>\n");
    }

    private static void RenderTable(string[] lines, ref int i, int end, StringBuilder sb)
    {
        static string[] Cells(string row)
        {
            var t = row.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|')) t = t[..^1];
            return t.Split('|').Select(c => c.Trim()).ToArray();
        }

        var header = Cells(lines[i]);
        var alignments = Cells(lines[i + 1]).Select(c =>
            c.StartsWith(':') && c.EndsWith(':') ? "center" : c.EndsWith(':') ? "right" : c.StartsWith(':') ? "left" : null).ToArray();
        string Align(int column) => column < alignments.Length && alignments[column] is { } a ? $" style=\"text-align:{a}\"" : "";

        sb.Append("<table>\n<thead><tr>");
        for (var c = 0; c < header.Length; c++)
            sb.Append($"<th{Align(c)}>").Append(Inline(header[c])).Append("</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        i += 2;
        while (i < end && lines[i].Trim().StartsWith('|'))
        {
            var cells = Cells(lines[i++]);
            sb.Append("<tr>");
            for (var c = 0; c < header.Length; c++)
                sb.Append($"<td{Align(c)}>").Append(c < cells.Length ? Inline(cells[c]) : "").Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
    }

    [GeneratedRegex(@"(`+)(.+?)\1")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;(.*?)&quot;)?\)")]
    private static partial Regex ImageLink();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;(.*?)&quot;)?\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<![\w""=/])(https?://[^\s<]+[^\s<.,:;""')\]])")]
    private static partial Regex AutoLink();

    /// <summary>Inline formatting: code spans are protected first, then the rest is escaped and formatted.</summary>
    public static string Inline(string text)
    {
        var codes = new List<string>();
        text = CodeSpan().Replace(text, m =>
        {
            codes.Add("<code>" + Encode(m.Groups[2].Value.Trim()) + "</code>");
            return $"\u0001{codes.Count - 1}\u0002";
        });

        text = Encode(text);
        text = ImageLink().Replace(text, m => $"<img src=\"{SafeUrl(m.Groups[2].Value)}\" alt=\"{m.Groups[1].Value}\"" +
                                              (m.Groups[3].Success ? $" title=\"{m.Groups[3].Value}\"" : "") + ">");
        text = Link().Replace(text, m => $"<a href=\"{SafeUrl(m.Groups[2].Value)}\"" + (m.Groups[3].Success ? $" title=\"{m.Groups[3].Value}\"" : "") +
                                         $">{m.Groups[1].Value}</a>");
        text = AutoLink().Replace(text, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        text = Regex.Replace(text, @"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", "<strong>$2</strong>");
        text = Regex.Replace(text, @"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?!\*)|(?<![\w_])_(?=\S)(.+?)(?<=\S)_(?![\w_])", m => $"<em>{m.Groups[1].Value}{m.Groups[2].Value}</em>");
        text = Regex.Replace(text, @"~~(?=\S)(.+?)(?<=\S)~~", "<del>$1</del>");
        text = Regex.Replace(text, @" {2,}\n|\\\n", "<br>\n");
        return Regex.Replace(text, "\u0001(\\d+)\u0002", m => codes[int.Parse(m.Groups[1].Value)]);
    }

    /// <summary>Blocks <c>javascript:</c> and other script URLs; the text is already HTML-encoded.</summary>
    private static string SafeUrl(string url)
    {
        var decoded = WebUtility.HtmlDecode(url).Trim();
        return Regex.IsMatch(decoded, @"^\s*(javascript|vbscript|data:text/html)", RegexOptions.IgnoreCase) ? "#" : url;
    }

    public static string Encode(string text) => WebUtility.HtmlEncode(text);

    public static string Slug(string text) =>
        Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"[^\w\s-]", ""), @"[\s]+", "-").Trim('-');
}
