using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;

namespace Dispatch.Infrastructure.Auth;

/// <summary>How the OAuth provider involves the user for browser-based grants.</summary>
public interface IOAuth2Interaction
{
    /// <summary>Opens the system browser at <paramref name="url"/>.</summary>
    void OpenBrowser(string url);

    /// <summary>Shows the device-code prompt ("go to URL and enter CODE").</summary>
    void ShowDeviceCode(string userCode, string verificationUri, string? verificationUriComplete);
}

public sealed class SystemBrowserInteraction : IOAuth2Interaction
{
    public event Action<string, string, string?>? DeviceCodeRequested;

    public void OpenBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void ShowDeviceCode(string userCode, string verificationUri, string? verificationUriComplete)
    {
        DeviceCodeRequested?.Invoke(userCode, verificationUri, verificationUriComplete);
        OpenBrowser(verificationUriComplete ?? verificationUri);
    }
}

/// <summary>
/// OAuth 2.0 tokens: client credentials, resource owner password, authorization code (with PKCE, via a loopback
/// redirect and the system browser) and device code. Cached tokens are reused until shortly before they expire, then
/// refreshed with the refresh token when there is one.
/// </summary>
public sealed class OAuth2TokenProvider(IHttpClientSource clients, IOAuth2Interaction interaction) : IOAuth2TokenProvider
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(5);

    public async Task<string> GetAccessTokenAsync(AuthSettings auth, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(auth.OAuth2AccessToken)
            && (auth.OAuth2ExpiresAt is null || auth.OAuth2ExpiresAt > DateTimeOffset.UtcNow + ExpirySkew))
            return auth.OAuth2AccessToken;

        if (string.IsNullOrWhiteSpace(auth.OAuth2TokenUrl))
            throw new InvalidOperationException("Set the token URL.");

        if (!string.IsNullOrEmpty(auth.OAuth2RefreshToken))
        {
            try
            {
                return await RequestTokenAsync(auth, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = auth.OAuth2RefreshToken
                }, ct).ConfigureAwait(false);
            }
            catch (OAuth2Exception)
            {
                // Refresh token expired or revoked: fall through to a full grant.
                auth.OAuth2RefreshToken = "";
            }
        }

        return auth.OAuth2GrantType switch
        {
            OAuth2GrantType.ClientCredentials => await RequestTokenAsync(auth, new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            }, ct).ConfigureAwait(false),
            OAuth2GrantType.Password => await RequestTokenAsync(auth, new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = auth.Username,
                ["password"] = auth.Password
            }, ct).ConfigureAwait(false),
            OAuth2GrantType.AuthorizationCode => await AuthorizationCodeAsync(auth, ct).ConfigureAwait(false),
            OAuth2GrantType.DeviceCode => await DeviceCodeAsync(auth, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported grant type {auth.OAuth2GrantType}.")
        };
    }

    private async Task<string> RequestTokenAsync(AuthSettings auth, Dictionary<string, string> form, CancellationToken ct)
    {
        var client = clients.GetClient(new RequestSettings());
        if (!string.IsNullOrWhiteSpace(auth.OAuth2Scope) && !form.ContainsKey("scope") && form["grant_type"] != "refresh_token")
            form["scope"] = auth.OAuth2Scope;
        if (!string.IsNullOrWhiteSpace(auth.OAuth2Audience))
            form["audience"] = auth.OAuth2Audience;

        using var message = new HttpRequestMessage(HttpMethod.Post, auth.OAuth2TokenUrl);
        ApplyClientAuthentication(message, auth, form);
        message.Content = new FormUrlEncodedContent(form);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        var json = TryParse(body);

        if (!response.IsSuccessStatusCode || json?["access_token"] is null)
        {
            var error = json?["error"]?.ToString();
            var description = json?["error_description"]?.ToString();
            throw new OAuth2Exception(error, error is null
                ? $"Token endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}"
                : $"{error}{(description is null ? "" : ": " + description)}");
        }

        auth.OAuth2AccessToken = json["access_token"]!.ToString();
        if (json["refresh_token"] is { } refresh)
            auth.OAuth2RefreshToken = refresh.ToString();
        auth.OAuth2ExpiresAt = json["expires_in"] is JsonValue expires && double.TryParse(expires.ToString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;
        return auth.OAuth2AccessToken;
    }

    private static void ApplyClientAuthentication(HttpRequestMessage message, AuthSettings auth, Dictionary<string, string> form)
    {
        if (auth.OAuth2CredentialsInBody || string.IsNullOrEmpty(auth.OAuth2ClientSecret))
        {
            form["client_id"] = auth.OAuth2ClientId;
            if (!string.IsNullOrEmpty(auth.OAuth2ClientSecret))
                form["client_secret"] = auth.OAuth2ClientSecret;
        }
        else
        {
            // RFC 6749 §2.3.1: form-encode the id and secret before Basic encoding.
            var credentials = $"{Uri.EscapeDataString(auth.OAuth2ClientId)}:{Uri.EscapeDataString(auth.OAuth2ClientSecret)}";
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
    }

    private async Task<string> AuthorizationCodeAsync(AuthSettings auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(auth.OAuth2AuthUrl))
            throw new InvalidOperationException("Set the authorization URL.");
        if (!Uri.TryCreate(auth.OAuth2RedirectUri, UriKind.Absolute, out var redirect) || !redirect.IsLoopback)
            throw new InvalidOperationException("The redirect URI must be a loopback address such as http://127.0.0.1:53682/callback.");

        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = auth.OAuth2ClientId,
            ["redirect_uri"] = auth.OAuth2RedirectUri,
            ["state"] = state
        };
        if (!string.IsNullOrWhiteSpace(auth.OAuth2Scope))
            query["scope"] = auth.OAuth2Scope;
        if (!string.IsNullOrWhiteSpace(auth.OAuth2Audience))
            query["audience"] = auth.OAuth2Audience;
        if (auth.OAuth2UsePkce)
        {
            query["code_challenge"] = challenge;
            query["code_challenge_method"] = "S256";
        }
        var separator = auth.OAuth2AuthUrl.Contains('?') ? "&" : "?";
        var authorizeUrl = auth.OAuth2AuthUrl + separator + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var prefix = $"{redirect.Scheme}://{redirect.Authority}{redirect.AbsolutePath.TrimEnd('/')}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        interaction.OpenBrowser(authorizeUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InteractiveTimeout);
        string code;
        while (true)
        {
            var contextTask = listener.GetContextAsync();
            var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
            if (finished != contextTask)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Timed out waiting for the browser sign-in.");
            }
            var context = await contextTask.ConfigureAwait(false);
            var parameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
            var error = parameters["error"];
            var returnedCode = parameters["code"];
            var ok = error is null && returnedCode is not null && parameters["state"] == state;
            await RespondAsync(context, ok
                ? "Signed in. You can close this tab and return to Dispatch."
                : $"Sign-in failed: {error ?? "missing code or state mismatch"}. You can close this tab.").ConfigureAwait(false);

            if (error is not null)
                throw new OAuth2Exception(error, $"{error}: {parameters["error_description"]}");
            if (returnedCode is null)
                continue; // e.g. favicon request
            if (parameters["state"] != state)
                throw new OAuth2Exception("invalid_state", "The state returned by the authorization server does not match.");
            code = returnedCode;
            break;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = auth.OAuth2RedirectUri
        };
        if (auth.OAuth2UsePkce)
            form["code_verifier"] = verifier;
        if (string.IsNullOrEmpty(auth.OAuth2ClientSecret))
            form["client_id"] = auth.OAuth2ClientId; // public client
        return await RequestTokenAsync(auth, form, ct).ConfigureAwait(false);
    }

    private async Task<string> DeviceCodeAsync(AuthSettings auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(auth.OAuth2DeviceUrl))
            throw new InvalidOperationException("Set the device authorization URL.");

        var client = clients.GetClient(new RequestSettings());
        var form = new Dictionary<string, string> { ["client_id"] = auth.OAuth2ClientId };
        if (!string.IsNullOrWhiteSpace(auth.OAuth2Scope))
            form["scope"] = auth.OAuth2Scope;
        if (!string.IsNullOrWhiteSpace(auth.OAuth2Audience))
            form["audience"] = auth.OAuth2Audience;

        using var deviceResponse = await client.PostAsync(auth.OAuth2DeviceUrl, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var device = TryParse(await deviceResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!deviceResponse.IsSuccessStatusCode || device?["device_code"] is null)
            throw new OAuth2Exception(device?["error"]?.ToString(), $"Device authorization failed: {device?["error_description"] ?? deviceResponse.ReasonPhrase}");

        var deviceCode = device["device_code"]!.ToString();
        var interval = TimeSpan.FromSeconds(device["interval"] is { } i && int.TryParse(i.ToString(), out var s) ? s : 5);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(device["expires_in"] is { } e && int.TryParse(e.ToString(), out var x) ? x : 600);
        interaction.ShowDeviceCode(device["user_code"]?.ToString() ?? "", device["verification_uri"]?.ToString()
                                                                           ?? device["verification_url"]?.ToString() ?? "",
            device["verification_uri_complete"]?.ToString());

        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            try
            {
                return await RequestTokenAsync(auth, new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode,
                    ["client_id"] = auth.OAuth2ClientId
                }, ct).ConfigureAwait(false);
            }
            catch (OAuth2Exception ex) when (ex.Error == "authorization_pending")
            {
            }
            catch (OAuth2Exception ex) when (ex.Error == "slow_down")
            {
                interval += TimeSpan.FromSeconds(5);
            }
        }
        throw new TimeoutException("The device code expired before sign-in completed.");
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var html = $"<!doctype html><html><body style=\"font-family:sans-serif;padding:40px\"><h2>Dispatch</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static JsonNode? TryParse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

public sealed class OAuth2Exception(string? error, string message) : Exception(message)
{
    public string? Error { get; } = error;
}
