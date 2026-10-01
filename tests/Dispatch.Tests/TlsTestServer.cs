using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dispatch.Tests;

/// <summary>An in-process HTTPS origin with a self-signed cert, for testing the capture proxy's TLS interception.</summary>
public sealed class TlsTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }

    private TlsTestServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public static async Task<TlsTestServer> StartAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var exportable = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0, o =>
        {
            o.Protocols = HttpProtocols.Http1;
            o.UseHttps(exportable);
        }));
        builder.Services.AddRouting();
        var app = builder.Build();
        app.MapGet("/secret", () => Results.Json(new { token = "abc123" }));
        await app.StartAsync();
        var uri = new Uri(app.Urls.First());
        return new TlsTestServer(app, uri.Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
