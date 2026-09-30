using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dispatch.Tests;

/// <summary>An in-process Kestrel server on a random port, for protocol integration tests.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }
    public string Host => new Uri(BaseUrl).Authority;

    /// <param name="http2Only">Cleartext HTTP/2 (h2c), as gRPC needs.</param>
    public static async Task<TestServer> StartAsync(Action<WebApplication> configure, bool http2Only = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0,
            o => o.Protocols = http2Only ? HttpProtocols.Http2 : HttpProtocols.Http1AndHttp2));
        builder.Services.AddRouting();
        var app = builder.Build();
        app.UseWebSockets();
        configure(app);
        await app.StartAsync();
        var address = app.Urls.First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new TestServer(app, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
