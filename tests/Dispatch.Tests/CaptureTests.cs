using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Dispatch.Application.Capture;
using Dispatch.Domain;
using Dispatch.Infrastructure.Capture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

public class CaptureConverterTests
{
    [Fact]
    public void Captured_exchange_becomes_a_request_with_body_and_example()
    {
        var exchange = new CapturedExchange
        {
            Method = "POST", Url = "https://api.example.com/users?page=2",
            RequestHeaders = [new("Host", "api.example.com"), new("Content-Type", "application/json"), new("Authorization", "Bearer x")],
            RequestBody = """{"name":"Ada"}""", RequestContentType = "application/json",
            StatusCode = 201, ResponseContentType = "application/json", ResponseBody = """{"id":1}"""
        };
        var request = CaptureConverter.ToRequest(exchange);

        Assert.Equal(HttpVerb.Post, request.Method);
        Assert.Equal(BodyMode.Json, request.Body.Mode);
        Assert.Contains(request.QueryParams, q => q.Key == "page" && q.Value == "2");
        Assert.DoesNotContain(request.Headers, h => h.Key == "Host");          // noise stripped
        Assert.Contains(request.Headers, h => h.Key == "Authorization");        // kept
        Assert.Single(request.Examples);
        Assert.Equal(201, request.Examples[0].StatusCode);
    }

    [Fact]
    public void Captured_form_body_is_decoded_so_it_is_not_encoded_twice_on_send()
    {
        var exchange = new CapturedExchange
        {
            Method = "POST", Url = "https://api.example.com/login",
            RequestBody = "user=ann%40x.io&note=a+b%26c", RequestContentType = "application/x-www-form-urlencoded"
        };
        var request = CaptureConverter.ToRequest(exchange);

        Assert.Equal(BodyMode.FormUrlEncoded, request.Body.Mode);
        Assert.Equal("ann@x.io", request.Body.FormFields.Single(f => f.Key == "user").Value);
        Assert.Equal("a b&c", request.Body.FormFields.Single(f => f.Key == "note").Value);
    }

    [Fact]
    public void Har_export_is_valid_and_contains_entries()
    {
        var har = CaptureConverter.ToHar(
        [
            new CapturedExchange { Method = "GET", Url = "https://x/a", StatusCode = 200, ReasonPhrase = "OK", ResponseBody = "hi", ResponseSize = 2 },
            new CapturedExchange { Method = "POST", Url = "https://x/b?q=1", RequestBody = "{}", RequestContentType = "application/json", StatusCode = 500 }
        ]);
        var node = JsonNode.Parse(har)!;
        Assert.Equal("1.2", node["log"]!["version"]!.GetValue<string>());
        var entries = node["log"]!["entries"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Equal("GET", entries[0]!["request"]!["method"]!.GetValue<string>());
        Assert.Equal(200, entries[0]!["response"]!["status"]!.GetValue<int>());
        Assert.Equal("q", entries[1]!["request"]!["queryString"]![0]!["name"]!.GetValue<string>());
    }
}

public class CertificateAuthorityTests
{
    [Fact]
    public void Mints_leaf_certs_signed_by_the_ca_with_sni()
    {
        using var ca = new CertificateAuthority();
        Assert.Contains("BEGIN CERTIFICATE", ca.CaCertificatePem);
        Assert.True(ca.CaCertificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);

        var leaf = ca.GetCertificate("api.example.com");
        Assert.Equal("CN=api.example.com", leaf.Subject);
        Assert.Equal(ca.CaCertificate.Subject, leaf.Issuer);
        Assert.True(leaf.HasPrivateKey);
        Assert.Same(leaf, ca.GetCertificate("api.example.com")); // cached
    }
}

[Collection("Console")]
public class CaptureProxyTests
{
    private static HttpClient ProxyClient(int port, CertificateAuthority? ca = null)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true
        };
        if (ca is not null)
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && CertMatchesCa(cert, ca);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static bool CertMatchesCa(X509Certificate2 presented, CertificateAuthority ca) =>
        presented.Issuer == ca.CaCertificate.Subject;

    [Fact]
    public async Task Captures_plain_http_request_and_response()
    {
        await using var origin = await TestServer.StartAsync(app =>
            app.MapPost("/echo", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                return Results.Json(new { got = body });
            }));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        var captured = new List<CapturedExchange>();
        proxy.Captured += captured.Add;
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0 });

        using var client = ProxyClient(proxy.Port);
        var response = await client.PostAsync($"{origin.BaseUrl}/echo", new StringContent("hello", System.Text.Encoding.UTF8, "text/plain"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello", text);
        var exchange = Assert.Single(captured);
        Assert.Equal("POST", exchange.Method);
        Assert.EndsWith("/echo", exchange.Url);
        Assert.Equal("hello", exchange.RequestBody);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Contains("got", exchange.ResponseBody);
    }

    [Fact]
    public async Task Decrypts_and_captures_https_via_connect()
    {
        await using var origin = await TestServer.StartAsync(app =>
            app.MapGet("/secret", () => Results.Json(new { token = "abc123" })));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        CapturedExchange? captured = null;
        proxy.Captured += e => captured = e;
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0, DecryptHttps = true });

        // Talk HTTPS to the origin *through* the proxy. The origin is HTTP, so rewrite scheme:
        // instead, tunnel to a TLS origin. Use the origin's URL but force https CONNECT by targeting a TLS test server.
        // Simpler: verify the proxy MITMs by connecting with https to a host the proxy forwards as http is not valid,
        // so we assert cert minting + CONNECT handling against a real TLS origin below.
        await using var tls = await TlsTestServer.StartAsync();
        using var client = ProxyClient(proxy.Port, ca);
        var response = await client.GetAsync($"https://localhost:{tls.Port}/secret");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("abc123", text);
        Assert.NotNull(captured);
        Assert.True(captured!.Secure);
        Assert.EndsWith("/secret", captured.Url);
        Assert.Contains("abc123", captured.ResponseBody);
    }

    [Fact]
    public async Task Host_filter_limits_what_is_recorded()
    {
        await using var origin = await TestServer.StartAsync(app => app.MapGet("/x", () => "ok"));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        var captured = new List<CapturedExchange>();
        proxy.Captured += captured.Add;
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0, HostFilter = "nonexistent.example" });

        using var client = ProxyClient(proxy.Port);
        await client.GetAsync($"{origin.BaseUrl}/x");
        Assert.Empty(captured); // host doesn't match the filter
    }
}

[Collection("Console")]
public class CliCaptureTests
{
    [Fact]
    public async Task Capture_command_exports_ca_pem()
    {
        var dir = Cli.TempDir();
        var ca = Path.Combine(dir, "ca.crt");
        // The proxy would block on Ctrl+C; run it on a background task and cancel quickly.
        using var cts = new CancellationTokenSource();
        var task = Dispatch.Cli.Program.Main(["capture", "--port", "0", "--export-ca", ca]);
        // The CA file is written before the proxy blocks. Wait until it is complete: it exists as soon as it is created,
        // before its content is written.
        var pem = "";
        for (var i = 0; i < 200 && !pem.Contains("END CERTIFICATE"); i++)
        {
            await Task.Delay(50);
            try
            {
                pem = File.Exists(ca) ? await File.ReadAllTextAsync(ca) : "";
            }
            catch (IOException)
            {
                // Still being written.
            }
        }
        Assert.Contains("BEGIN CERTIFICATE", pem);
    }
}

[Collection("Console")]
public class CaptureProxyRelayTests
{
    private static HttpClient ProxyClient(int port) => new(new HttpClientHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{port}"),
        UseProxy = true,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(15) };

    [Fact]
    public async Task Multi_valued_response_headers_are_relayed_one_per_line()
    {
        await using var origin = await TestServer.StartAsync(app => app.MapGet("/cookies", (HttpContext ctx) =>
        {
            ctx.Response.Headers.Append("Set-Cookie", "a=1; Path=/; Expires=Wed, 21 Oct 2026 07:28:00 GMT");
            ctx.Response.Headers.Append("Set-Cookie", "b=2; Path=/; HttpOnly");
            return "ok";
        }));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        CapturedExchange? captured = null;
        proxy.Captured += e => captured = e;
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0 });

        using var client = ProxyClient(proxy.Port);
        using var response = await client.GetAsync($"{origin.BaseUrl}/cookies");

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Equal(2, setCookies.Count);
        Assert.Contains(setCookies, c => c.StartsWith("a=1; Path=/; Expires=Wed, 21 Oct 2026"));
        Assert.Contains(setCookies, c => c.StartsWith("b=2"));
        Assert.NotNull(captured);
        Assert.Equal(2, captured!.ResponseHeaders.Count(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Large_request_bodies_are_forwarded_whole_and_only_the_recording_is_capped()
    {
        await using var origin = await TestServer.StartAsync(app => app.MapPost("/upload", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            return Results.Json(new { length = ms.Length });
        }));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        CapturedExchange? captured = null;
        proxy.Captured += e => captured = e;
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0, MaxBodyBytes = 1024 });

        var payload = new string('x', 10_000); // > MaxBodyBytes * 4
        using var client = ProxyClient(proxy.Port);
        using var response = await client.PostAsync($"{origin.BaseUrl}/upload", new StringContent(payload, System.Text.Encoding.UTF8, "text/plain"));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10_000, json["length"]!.GetValue<long>());
        Assert.NotNull(captured);
        Assert.Equal(1024, captured!.RequestBody.Length);
    }

    [Theory]
    [InlineData("api.example.com:8443", "api.example.com", 8443)]
    [InlineData("api.example.com", "api.example.com", 443)]
    [InlineData("[::1]:8443", "::1", 8443)]
    [InlineData("[2001:db8::1]", "2001:db8::1", 443)]
    [InlineData("10.0.0.5:443", "10.0.0.5", 443)]
    public void Connect_authority_parsing_keeps_ipv6_literals_intact(string authority, string host, int port)
    {
        Assert.True(CaptureProxy.TryParseConnectAuthority(authority, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:notaport")]
    [InlineData("host/path")]
    public void Connect_authority_parsing_rejects_garbage(string authority) =>
        Assert.False(CaptureProxy.TryParseConnectAuthority(authority, out _, out _));
}

public class PersistedCertificateAuthorityTests
{
    [Fact]
    public void Persisted_ca_is_reused_across_loads_and_a_corrupt_file_is_regenerated()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dispatch-ca-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "capture-ca.pfx");
        try
        {
            using var first = CertificateAuthority.LoadOrCreate(path);
            using var second = CertificateAuthority.LoadOrCreate(path);
            Assert.True(File.Exists(path));
            Assert.Equal(first.Thumbprint, second.Thumbprint);
            Assert.True(second.HasPrivateKey);
            using (var ca = new CertificateAuthority(CertificateAuthority.LoadOrCreate(path)))
                Assert.Equal(first.Subject, ca.GetCertificate("example.test").Issuer); // the loaded key can sign leaves

            File.WriteAllText(path, "this is not a pkcs#12 file");
            using var regenerated = CertificateAuthority.LoadOrCreate(path);
            Assert.NotEqual(first.Thumbprint, regenerated.Thumbprint);
            using var reloaded = CertificateAuthority.LoadOrCreate(path);
            Assert.Equal(regenerated.Thumbprint, reloaded.Thumbprint); // the bad file was replaced by a valid one
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
