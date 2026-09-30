using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dispatch.Application.Auth;

public sealed record JwtInfo(
    string HeaderJson,
    string PayloadJson,
    string Algorithm,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt,
    string? Subject,
    string? Issuer,
    IReadOnlyList<string> Audience)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } exp && exp <= now;

    public string Summary(DateTimeOffset now)
    {
        var parts = new List<string> { $"alg {Algorithm}" };
        if (Subject is not null) parts.Add($"sub {Subject}");
        if (Issuer is not null) parts.Add($"iss {Issuer}");
        if (ExpiresAt is { } exp)
            parts.Add(exp <= now ? $"expired {exp.ToLocalTime():g}" : $"expires {exp.ToLocalTime():g} (in {Humanize(exp - now)})");
        return string.Join(" · ", parts);
    }

    private static string Humanize(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(0, (int)t.TotalMinutes)}m";
}

/// <summary>Decodes (without verifying) a JSON Web Token, for inspection.</summary>
public static class Jwt
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool LooksLikeJwt(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            t = t[7..].Trim();
        var parts = t.Split('.');
        return parts.Length == 3 && parts[0].StartsWith("eyJ", StringComparison.Ordinal) && parts[1].Length > 0;
    }

    public static JwtInfo Decode(string token)
    {
        token = token.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        var parts = token.Split('.');
        if (parts.Length is < 2 or > 3)
            throw new FormatException("A JWT has three dot-separated parts.");

        var header = ParsePart(parts[0], "header");
        var payload = ParsePart(parts[1], "payload");

        return new JwtInfo(
            header.ToJsonString(Pretty),
            payload.ToJsonString(Pretty),
            header["alg"]?.ToString() ?? "none",
            Time(payload["iat"]),
            Time(payload["nbf"]),
            Time(payload["exp"]),
            payload["sub"]?.ToString(),
            payload["iss"]?.ToString(),
            payload["aud"] switch
            {
                JsonArray a => a.Select(x => x?.ToString() ?? "").ToList(),
                JsonNode n => [n.ToString()],
                null => []
            });
    }

    private static JsonNode ParsePart(string part, string name)
    {
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(Base64Url(part))) ?? throw new FormatException($"Empty JWT {name}.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new FormatException($"The JWT {name} is not valid base64url JSON.");
        }
    }

    private static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue v && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
            : null;

    public static byte[] Base64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}
