namespace Dispatch.Application.Formatting;

public enum PreviewKind
{
    None,
    Image,
    Svg,
    Html,
    Pdf,
    Binary
}

/// <summary>Decides how a response body can be previewed and which file extension suits it.</summary>
public static class BodyPreview
{
    public static PreviewKind Detect(string? contentType, byte[]? bytes, string body)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";
        if (type == "image/svg+xml" || (type.Length == 0 && body.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase)))
            return PreviewKind.Svg;
        if (type == "application/pdf" || (bytes is { Length: > 4 } && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F'))
            return PreviewKind.Pdf;
        if (type.StartsWith("image/") || (bytes is not null && SniffImage(bytes)))
            return PreviewKind.Image;
        if (type.Contains("html") || (type.Length == 0 && body.TrimStart().StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)))
            return PreviewKind.Html;
        return bytes is not null ? PreviewKind.Binary : PreviewKind.None;
    }

    private static bool SniffImage(byte[] b) =>
        (b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        || (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        || (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
        || (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        || (b.Length > 2 && b[0] == 'B' && b[1] == 'M');

    /// <summary>A file extension (with dot) for saving the body.</summary>
    public static string Extension(string? contentType, BodyFormat format)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";
        return type switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "application/gzip" => ".gz",
            "text/csv" => ".csv",
            "application/octet-stream" => ".bin",
            _ => format switch
            {
                BodyFormat.Json => ".json",
                BodyFormat.Xml => ".xml",
                BodyFormat.Html => ".html",
                BodyFormat.JavaScript => ".js",
                _ => ".txt"
            }
        };
    }

    /// <summary>Readable text of an HTML page (scripts and styles dropped, block elements on their own lines).</summary>
    public static string HtmlToText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<(br|/p|/div|/h\d|/li|/tr|/title)[^>]*>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<li[^>]*>", "• ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"[ \t\r]+", " ").Trim());
        return System.Text.RegularExpressions.Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
    }
}
