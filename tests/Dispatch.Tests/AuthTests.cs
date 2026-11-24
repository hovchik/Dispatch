using System.Text;
using System.Text.Json.Nodes;
using Dispatch.Application.Auth;
using Dispatch.Domain;
using Dispatch.Infrastructure.Auth;
using Dispatch.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

public sealed class OAuth2Tests : IAsyncLifetime
{
    private TestServer _server = null!;
    private readonly HttpClientPool _pool = new();
    private readonly List<Dictionary<string, string>> _tokenRequests = [];
    private string? _lastAuthorization;
    private int _devicePolls;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/token", async (HttpContext ctx) =>
            {
                var form = (await ctx.Request.ReadFormAsync()).ToDictionary(f => f.Key, f => f.Value.ToString());
                lock (_tokenRequests)
                    _tokenRequests.Add(form);
                _lastAuthorization = ctx.Request.Headers.Authorization;
                ctx.Response.ContentType = "application/json";

                switch (form["grant_type"])
                {
                    case "urn:ietf:params:oauth:grant-type:device_code" when ++_devicePolls < 2:
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsync("""{"error":"authorization_pending"}""");
                        return;
                    case "authorization_code" when form.GetValueOrDefault("code") != "the-code":
                    case "password" when form.GetValueOrDefault("password") != "pw":
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsync("""{"error":"invalid_grant","error_description":"bad credentials"}""");
                        return;
                }
                var n = _tokenRequests.Count;
                await ctx.Response.WriteAsync($"{{\"access_token\":\"at-{n}\",\"refresh_token\":\"rt-{n}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}");
            });
            app.MapPost("/device", () => Results.Json(new
            {
                device_code = "dc", user_code = "ABCD-EFGH", verification_uri = "https://example.com/device", interval = 1, expires_in = 60
            }));
        });
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _pool.Dispose();
    }

    private sealed class FakeBrowser(HttpClientPool pool) : IOAuth2Interaction
    {
        public string? DeviceUserCode { get; private set; }

        public void OpenBrowser(string url)
        {
            // Simulate the authorization server redirecting back to the loopback listener.
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            var redirect = $"{query["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(query["state"]!)}";
            Assert.Equal("S256", query["code_challenge_method"]);
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                await pool.GetClient(new RequestSettings()).GetAsync(redirect);
            });
        }

        public void ShowDeviceCode(string userCode, string verificationUri, string? verificationUriComplete) => DeviceUserCode = userCode;
    }

    private AuthSettings Auth(OAuth2GrantType grant) => new()
    {
        Mode = AuthMode.OAuth2,
        OAuth2GrantType = grant,
        OAuth2TokenUrl = _server.BaseUrl + "/token",
        OAuth2DeviceUrl = _server.BaseUrl + "/device",
        OAuth2AuthUrl = "https://login.example.com/authorize",
        OAuth2RedirectUri = $"http://127.0.0.1:{HttpProtocolExecutorTests.FreePort()}/callback",
        OAuth2ClientId = "my app",
        OAuth2ClientSecret = "s3cret",
        OAuth2Scope = "read write",
        Username = "ann",
        Password = "pw"
    };

    [Fact]
    public async Task Client_credentials_uses_basic_auth_and_caches_the_token()
    {
        var provider = new OAuth2TokenProvider(_pool, new FakeBrowser(_pool));
        var auth = Auth(OAuth2GrantType.ClientCredentials);

        var first = await provider.GetAccessTokenAsync(auth, CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(auth, CancellationToken.None);

        Assert.Equal("at-1", first);
        Assert.Equal(first, second);
        Assert.Single(_tokenRequests);
        Assert.Equal("read write", _tokenRequests[0]["scope"]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("my%20app:s3cret")), _lastAuthorization);
        Assert.True(auth.OAuth2ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(59));
    }

    [Fact]
    public async Task Expired_token_is_refreshed_with_the_refresh_token()
    {
        var provider = new OAuth2TokenProvider(_pool, new FakeBrowser(_pool));
        var auth = Auth(OAuth2GrantType.Password);
        await provider.GetAccessTokenAsync(auth, CancellationToken.None);
        auth.OAuth2ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        var refreshed = await provider.GetAccessTokenAsync(auth, CancellationToken.None);

        Assert.Equal("at-2", refreshed);
        Assert.Equal("refresh_token", _tokenRequests[1]["grant_type"]);
        Assert.Equal("rt-1", _tokenRequests[1]["refresh_token"]);
    }

    [Fact]
    public async Task Error_responses_surface_error_and_description()
    {
        var auth = Auth(OAuth2GrantType.Password);
        auth.Password = "wrong";

        var ex = await Assert.ThrowsAsync<OAuth2Exception>(() =>
            new OAuth2TokenProvider(_pool, new FakeBrowser(_pool)).GetAccessTokenAsync(auth, CancellationToken.None));

        Assert.Equal("invalid_grant: bad credentials", ex.Message);
    }

    [Fact]
    public async Task Authorization_code_with_pkce_via_loopback_redirect()
    {
        var token = await new OAuth2TokenProvider(_pool, new FakeBrowser(_pool))
            .GetAccessTokenAsync(Auth(OAuth2GrantType.AuthorizationCode), CancellationToken.None);

        Assert.StartsWith("at-", token);
        var exchange = _tokenRequests.Single();
        Assert.Equal("the-code", exchange["code"]);
        Assert.True(exchange["code_verifier"].Length >= 43);
    }

    [Fact]
    public async Task Device_code_polls_until_authorized()
    {
        var browser = new FakeBrowser(_pool);
        var token = await new OAuth2TokenProvider(_pool, browser).GetAccessTokenAsync(Auth(OAuth2GrantType.DeviceCode), CancellationToken.None);

        Assert.StartsWith("at-", token);
        Assert.Equal("ABCD-EFGH", browser.DeviceUserCode);
        Assert.Equal(2, _devicePolls);
    }
}

public class JwtTests
{
    [Fact]
    public void Decodes_claims_and_expiry()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Part("""{"alg":"RS256","typ":"JWT"}""")}.{Part("""{"sub":"42","iss":"idp","aud":["a","b"],"exp":1700000000,"name":"Ann"}""")}.sig";

        Assert.True(Jwt.LooksLikeJwt("Bearer " + token));
        var info = Jwt.Decode("Bearer " + token);

        Assert.Equal("RS256", info.Algorithm);
        Assert.Equal("42", info.Subject);
        Assert.Equal(["a", "b"], info.Audience);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), info.ExpiresAt);
        Assert.True(info.IsExpired(DateTimeOffset.UtcNow));
        Assert.Equal("Ann", JsonNode.Parse(info.PayloadJson)!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Millisecond_and_out_of_range_time_claims_do_not_throw()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var ms = Jwt.Decode($"{Part("""{"alg":"HS256"}""")}.{Part("""{"exp":1700000000000,"iat":1600000000000}""")}.sig");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), ms.ExpiresAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000), ms.IssuedAt);

        var absurd = Jwt.Decode($"{Part("""{"alg":"HS256"}""")}.{Part("""{"exp":1e30}""")}.sig");
        Assert.Null(absurd.ExpiresAt);
    }
}

public sealed class DigestAuthTests
{
    [Fact]
    public async Task Digest_credentials_answer_the_challenge()
    {
        const string realm = "test";
        await using var server = await TestServer.StartAsync(app => app.MapGet("/secure", (HttpContext ctx) =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Digest ", StringComparison.Ordinal))
            {
                ctx.Response.Headers.WWWAuthenticate = $"Digest realm=\"{realm}\", nonce=\"abc123\", qop=\"auth\", algorithm=MD5";
                return Results.StatusCode(401);
            }
            return header.Contains("username=\"ann\"") && header.Contains("response=") ? Results.Ok("in") : Results.StatusCode(403);
        }));

        using var pool = new HttpClientPool();
        var executor = new HttpProtocolExecutor(new Dispatch.Application.Requests.RequestMessageBuilder(), new HttpRequestExecutor(pool));
        var response = await executor.SendAsync(new ApiRequest
        {
            Url = server.BaseUrl + "/secure",
            Auth = new AuthSettings { Mode = AuthMode.Digest, Username = "ann", Password = "pw" }
        }, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
    }
}

public class BearerTokenTests
{
    private static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Token(long exp, string sub = "42") =>
        $"{Part("""{"alg":"RS256","typ":"JWT"}""")}.{Part($$"""{"sub":"{{sub}}","exp":{{exp}}}""")}.c2ln";

    private static readonly long Future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

    [Fact]
    public void Finds_the_access_token_of_a_token_response()
    {
        var access = Token(Future);
        var body = $$"""
            {
              "id_token": "{{Token(Future, "id")}}",
              "access_token": "{{access}}",
              "expires_in": 3600,
              "token_type": "Bearer",
              "refresh_token": null
            }
            """;

        var match = BearerTokens.FindJwt(body);

        Assert.NotNull(match);
        Assert.Equal(access, match.Token);
        Assert.Equal("access_token", match.Field);
    }

    [Fact]
    public void Finds_nested_and_bearer_prefixed_tokens_and_plain_text_ones()
    {
        var token = Token(Future);
        var nested = BearerTokens.FindJwt("""{"data":{"session":{"jwt":"Bearer """ + token + "\"}}}");
        Assert.Equal(token, nested?.Token);
        Assert.Equal("jwt", nested?.Field);

        var text = BearerTokens.FindJwt($"token={token}; path=/");
        Assert.Equal(token, text?.Token);
        Assert.Null(text?.Field);

        Assert.Null(BearerTokens.FindJwt("""{"name":"eyJ not a token"}"""));
        Assert.Null(BearerTokens.FindJwt("""{"value":"eyJabc.eyJdef.ghi"}""")); // JWT-shaped but not decodable
    }

    [Fact]
    public void Suggests_filling_new_requests_and_bearer_requests_without_a_token()
    {
        var token = Token(Future);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(TokenSuggestion.Fill, BearerTokens.Suggest(new AuthSettings(), isNewRequest: true, token, now));
        Assert.Equal(TokenSuggestion.None, BearerTokens.Suggest(new AuthSettings(), isNewRequest: false, token, now));
        Assert.Equal(TokenSuggestion.Fill, BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.Bearer }, false, token, now));
        Assert.Equal(TokenSuggestion.Fill, BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.Bearer }, false, "Bearer " + token, now));
    }

    [Fact]
    public void Suggests_replacing_another_literal_token_but_leaves_variables_and_other_auth_alone()
    {
        var token = Token(Future);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(TokenSuggestion.Replace,
            BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.Bearer, Token = Token(Future, "old") }, false, token, now));
        Assert.Equal(TokenSuggestion.None,
            BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.Bearer, Token = token }, false, token, now));
        Assert.Equal(TokenSuggestion.None,
            BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.Bearer, Token = "Bearer " + token }, false, token, now));
        Assert.Equal(TokenSuggestion.None,
            BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" }, false, token, now));
        Assert.Equal(TokenSuggestion.None,
            BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.Basic }, true, token, now));
        Assert.Equal(TokenSuggestion.None,
            BearerTokens.Suggest(new AuthSettings { Mode = AuthMode.OAuth2 }, true, token, now));
    }

    [Fact]
    public void Never_suggests_expired_or_non_jwt_text()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TokenSuggestion.None, BearerTokens.Suggest(new AuthSettings(), true, Token(now.AddMinutes(-1).ToUnixTimeSeconds()), now));
        Assert.Equal(TokenSuggestion.None, BearerTokens.Suggest(new AuthSettings(), true, "hello world", now));
    }

    [Fact]
    public void Apply_sets_bearer_auth_with_the_bare_token()
    {
        var token = Token(Future);
        var auth = new AuthSettings { Mode = AuthMode.None };

        BearerTokens.Apply(auth, "Bearer " + token);

        Assert.Equal(AuthMode.Bearer, auth.Mode);
        Assert.Equal(token, auth.Token);
        Assert.Equal("Bearer " + token, BearerTokens.AsBearer(token));
    }

    [Fact]
    public void Finds_the_token_in_the_example_login_response()
    {
        const string body = """
            {
              "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzY29wZSI6InFyUmVnaXN0ZXJQZm8iLCJmb2lkIjoiMTAwNiIsIm5hbWVpZCI6IjIwMTkiLCJjbGllbnRfaWQiOiJJZHJhbVFSUGZvIiwibmJmIjoxNzk1NTEzMzg1LCJleHAiOjE3OTU1MTY5ODUsImlhdCI6MTc5NTUxMzM4NX0.FoNk3IewSbuNMk2ftHY_z78JWD0hQJa3QFj1xIBjjDr6RCj4ghq_v4BQQAiXZFZpZJ_XU8YbvBneYl2yVrgJfg7wqabfGb4qY_2wQ6E4fkfh5E8zaowPLgmeR6uVnv2vjQoCcKWXZ75VJ0781_KKvGKBjcFUqFzvoL8InyLVUewcBR_FpkMKDH3gjn3GUfKvt0pXYPLs1pMXH-PL8eEd5RNrSL1OnqnA4PmttQbx3nddVAqRJPCZPqcc61U8Brk3sg2wK-JBXzukNXr5Yzn0dGsKH8R4k8lT4wM2uqteSfdFqzK5mWR4grJiVoFWI2NUFEjNGi0bJn3YcL03arteQg",
              "expires_in": 3600,
              "token_type": "Bearer",
              "refresh_token": null
            }
            """;

        var match = BearerTokens.FindJwt(body);

        Assert.Equal("access_token", match?.Field);
        Assert.Equal("RS256", Jwt.Decode(match!.Token).Algorithm);
    }
}

public class JwtShapeTests
{
    [Fact]
    public void A_payload_that_is_not_an_object_is_not_a_token()
    {
        const string arrayPayload = "eyJhbGciOiJIUzI1NiJ9.W10.c2ln"; // payload "[]"
        Assert.Throws<FormatException>(() => Jwt.Decode(arrayPayload));
        Assert.False(BearerTokens.IsDecodable(arrayPayload));
        Assert.Null(BearerTokens.FindJwt($$"""{"token":"{{arrayPayload}}"}"""));
        Assert.Equal(TokenSuggestion.None, BearerTokens.Suggest(new AuthSettings(), true, arrayPayload, DateTimeOffset.UtcNow));
    }
}
