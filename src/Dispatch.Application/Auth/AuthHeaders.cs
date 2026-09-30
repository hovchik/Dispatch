using System.Text;
using Dispatch.Domain;

namespace Dispatch.Application.Auth;

/// <summary>Auth expressed as plain headers / query parameters, for protocols that don't use HttpRequestMessage.</summary>
public static class AuthHeaders
{
    public static IEnumerable<KeyValuePair<string, string>> Headers(AuthSettings auth)
    {
        switch (auth.Mode)
        {
            case AuthMode.Bearer when !string.IsNullOrWhiteSpace(auth.Token):
                yield return new("Authorization", "Bearer " + auth.Token.Trim());
                break;
            case AuthMode.Basic:
                yield return new("Authorization",
                    "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}")));
                break;
            case AuthMode.ApiKey when auth.ApiKeyLocation == ApiKeyLocation.Header && !string.IsNullOrWhiteSpace(auth.ApiKeyName):
                yield return new(auth.ApiKeyName.Trim(), auth.ApiKeyValue);
                break;
        }
    }

    /// <summary>Appends an API key query parameter when the auth is configured that way.</summary>
    public static string ApplyQuery(string url, AuthSettings auth)
    {
        if (auth is not { Mode: AuthMode.ApiKey, ApiKeyLocation: ApiKeyLocation.QueryParam }
            || string.IsNullOrWhiteSpace(auth.ApiKeyName))
            return url;
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{Uri.EscapeDataString(auth.ApiKeyName)}={Uri.EscapeDataString(auth.ApiKeyValue)}";
    }

    /// <summary>Active request headers plus auth headers (auth wins on conflicts).</summary>
    public static List<KeyValuePair<string, string>> Combined(ApiRequest request)
    {
        var headers = request.Headers.Where(h => h.IsActive)
            .Select(h => new KeyValuePair<string, string>(h.Key.Trim(), h.Value))
            .ToList();
        foreach (var header in Headers(request.Auth))
        {
            headers.RemoveAll(h => string.Equals(h.Key, header.Key, StringComparison.OrdinalIgnoreCase));
            headers.Add(header);
        }
        return headers;
    }
}
