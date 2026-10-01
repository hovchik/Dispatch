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
        // The CA file is written synchronously before the proxy blocks.
        for (var i = 0; i < 50 && !File.Exists(ca); i++)
            await Task.Delay(50);
        Assert.True(File.Exists(ca));
        Assert.Contains("BEGIN CERTIFICATE", await File.ReadAllTextAsync(ca));
    }
}
