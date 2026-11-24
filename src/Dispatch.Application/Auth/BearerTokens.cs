using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatch.Domain;

namespace Dispatch.Application.Auth;

/// <summary>A JWT found in a response body, and the JSON field it came from (e.g. "access_token").</summary>
public sealed record JwtMatch(string Token, string? Field);

/// <summary>What the request tab should offer for a token the user copied.</summary>
public enum TokenSuggestion
{
    None,

    /// <summary>The request has no token yet: offer to fill Auth with it.</summary>
    Fill,

    /// <summary>The request already sends a different literal token: offer to replace it.</summary>
    Replace
}

/// <summary>Finds bearer tokens in responses and decides when to offer a copied one for a request's Auth.</summary>
public static partial class BearerTokens
{
    /// <summary>Field names token endpoints use, checked first so access_token wins over id_token or refresh_token.</summary>
    private static readonly string[] PreferredFields = ["access_token", "accessToken", "token", "jwt", "id_token", "idToken"];

    private const int MaxBodyLength = 200_000;

    /// <summary>
    /// Finds a JWT in a response body. JSON bodies are walked so the field name is known and access_token is preferred;
    /// other bodies fall back to the first JWT-shaped text.
    /// </summary>
    public static JwtMatch? FindJwt(string? body)
    {
        if (string.IsNullOrEmpty(body) || body.Length > MaxBodyLength || !body.Contains("eyJ", StringComparison.Ordinal))
            return null;

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var found = new List<(string Field, string Token)>();
                Collect(doc.RootElement, null, found);
                if (found.Count > 0)
                {
                    foreach (var preferred in PreferredFields)
                        if (found.FirstOrDefault(f => string.Equals(f.Field, preferred, StringComparison.OrdinalIgnoreCase)) is { Token: not null } hit)
                            return new JwtMatch(hit.Token, hit.Field);
                    return new JwtMatch(found[0].Token, found[0].Field);
                }
                return null;
            }
            catch (JsonException)
            {
                // Not valid JSON after all: fall through to the text scan.
            }
        }

        var match = JwtText().Match(body);
        return match.Success && IsDecodable(match.Value) ? new JwtMatch(match.Value, null) : null;
    }

    private static void Collect(JsonElement element, string? name, List<(string, string)> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, property.Name, found);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, name, found);
                break;
            case JsonValueKind.String:
                var token = Normalize(element.GetString());
                if (token is not null && IsDecodable(token))
                    found.Add((name ?? "", token));
                break;
        }
    }

    /// <summary>The bare token: trimmed, without a "Bearer " prefix; null when it is not JWT-shaped.</summary>
    public static string? Normalize(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            t = t[7..].Trim();
        return Jwt.LooksLikeJwt(t) && !t.Any(char.IsWhiteSpace) ? t : null;
    }

    public static bool IsDecodable(string token)
    {
        try
        {
            Jwt.Decode(token);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>"Bearer &lt;token&gt;", ready for an Authorization header.</summary>
    public static string AsBearer(string token) => "Bearer " + token.Trim();

    /// <summary>
    /// Whether to offer <paramref name="token"/> for a request's Auth. Requests on Bearer get it when their token is
    /// empty or a different literal token (a {{variable}} is left alone). Requests without auth get it only while new,
    /// since a saved request on "None" most likely needs none. Other auth types are the user's choice and expired
    /// tokens are never offered.
    /// </summary>
    public static TokenSuggestion Suggest(AuthSettings auth, bool isNewRequest, string token, DateTimeOffset now)
    {
        if (Normalize(token) is not { } bare)
            return TokenSuggestion.None;
        try
        {
            if (Jwt.Decode(bare).IsExpired(now))
                return TokenSuggestion.None;
        }
        catch (FormatException)
        {
            return TokenSuggestion.None;
        }

        switch (auth.Mode)
        {
            case AuthMode.Bearer:
                var current = auth.Token.Trim();
                if (current.Length == 0)
                    return TokenSuggestion.Fill;
                if (current.Contains("{{", StringComparison.Ordinal))
                    return TokenSuggestion.None;
                return string.Equals(Normalize(current) ?? current, bare, StringComparison.Ordinal)
                    ? TokenSuggestion.None
                    : TokenSuggestion.Replace;
            case AuthMode.None:
                return isNewRequest ? TokenSuggestion.Fill : TokenSuggestion.None;
            default:
                return TokenSuggestion.None;
        }
    }

    /// <summary>Puts the token into the request's Auth as a bearer token.</summary>
    public static void Apply(AuthSettings auth, string token)
    {
        auth.Mode = AuthMode.Bearer;
        auth.Token = Normalize(token) ?? token.Trim();
    }

    [GeneratedRegex(@"eyJ[\w-]+\.eyJ[\w-]+\.[\w-]*")]
    private static partial Regex JwtText();
}
