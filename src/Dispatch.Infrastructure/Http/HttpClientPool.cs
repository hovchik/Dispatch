using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Dispatch.Domain;

namespace Dispatch.Infrastructure.Http;

/// <summary>Supplies an <see cref="HttpClient"/> configured for a request's transport settings.</summary>
public interface IHttpClientSource
{
    HttpClient GetClient(RequestSettings settings, NetworkCredential? credentials = null);
}

/// <summary>Connection phase timings, filled in by the connect callback when a new connection is opened.</summary>
public sealed class ConnectionTimings
{
    public static readonly HttpRequestOptionsKey<ConnectionTimings> Key = new("dispatch.timings");

    public TimeSpan? Dns { get; set; }
    public TimeSpan? Connect { get; set; }
    public long ConnectedAt { get; set; }
}

/// <summary>
/// Caches one handler per distinct transport configuration (redirects, TLS, proxy, client certificate, credentials),
/// so per-request settings don't cost a new connection pool on every send.
/// </summary>
public sealed class HttpClientPool : IHttpClientSource, IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public HttpClient GetClient(RequestSettings settings, NetworkCredential? credentials = null)
    {
        var key = settings.TransportKey() + "|" + (credentials is null
            ? ""
            : $"{credentials.Domain}\\{credentials.UserName}:{credentials.Password.GetHashCode()}");
        return _clients.GetOrAdd(key, _ => new HttpClient(CreateHandler(settings, credentials), disposeHandler: true)
        {
            // Timeouts are applied per request (RequestSettings.TimeoutMs).
            Timeout = Timeout.InfiniteTimeSpan
        });
    }

    internal static SocketsHttpHandler CreateHandler(RequestSettings settings, NetworkCredential? credentials)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = settings.FollowRedirects,
            MaxAutomaticRedirections = Math.Max(1, settings.MaxRedirects),
            // An API client must send exactly the headers the user configured; the cookie jar is applied explicitly.
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            ConnectCallback = ConnectAsync
        };

        if (credentials is not null)
        {
            handler.Credentials = credentials;
            handler.PreAuthenticate = false;
        }

        if (!string.IsNullOrWhiteSpace(settings.Proxy))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(settings.Proxy);
        }
        else
        {
            handler.UseProxy = settings.UseSystemProxy;
        }

        var ssl = new SslClientAuthenticationOptions();
        if (!settings.VerifySsl)
            ssl.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        if (!string.IsNullOrWhiteSpace(settings.ClientCertificatePath))
        {
            var certificate = LoadCertificate(settings);
            ssl.ClientCertificates = [certificate];
            ssl.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }
        handler.SslOptions = ssl;
        return handler;
    }

    public static X509Certificate2 LoadCertificate(RequestSettings settings)
    {
        var path = settings.ClientCertificatePath;
        var password = string.IsNullOrEmpty(settings.ClientCertificatePassword) ? null : settings.ClientCertificatePassword;
        if (!File.Exists(path))
            throw new FileNotFoundException($"Client certificate not found: {path}");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pem" or ".crt" or ".cer")
        {
            var keyPath = string.IsNullOrWhiteSpace(settings.ClientCertificateKeyPath) ? path : settings.ClientCertificateKeyPath;
            var pem = password is null
                ? X509Certificate2.CreateFromPemFile(path, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath);
            // Round-trip through PKCS#12 so the private key is usable by SslStream on every platform.
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        context.InitialRequestMessage.Options.TryGetValue(ConnectionTimings.Key, out var timings);
        var start = Stopwatch.GetTimestamp();

        var endpoint = context.DnsEndPoint;
        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literal))
            addresses = [literal];
        else
            addresses = await System.Net.Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        var resolvedAt = Stopwatch.GetTimestamp();
        if (timings is not null)
            timings.Dns = Stopwatch.GetElapsedTime(start, resolvedAt);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        if (timings is not null)
        {
            timings.ConnectedAt = Stopwatch.GetTimestamp();
            timings.Connect = Stopwatch.GetElapsedTime(resolvedAt, timings.ConnectedAt);
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }
}
