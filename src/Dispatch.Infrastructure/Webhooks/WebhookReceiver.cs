using System.Net;
using System.Text;
using Dispatch.Application.Webhooks;
using Dispatch.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Dispatch.Infrastructure.Webhooks;

/// <summary>
/// Kestrel host behind <see cref="IWebhookReceiver"/>: records every request (any method or path) into the log and
/// answers with the configured status and body. <c>GET /</c> with no hooks path shows a short status page so a browser
/// (or a tunnel's health check) can see it is alive.
/// </summary>
public sealed class WebhookReceiver : IWebhookReceiver, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;

    public WebhookLog Log { get; private set; } = new();
    public bool IsRunning => _app is not null;
    public Uri? BaseUrl { get; private set; }
    public int? Port { get; private set; }
    public WebhookReceiverOptions Options { get; private set; } = new();

    public event Action? StateChanged;

    public async Task StartAsync(WebhookReceiverOptions options, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is not null)
                throw new InvalidOperationException("The webhook receiver is already running.");
            Options = options;
            if (Log.Capacity != options.Capacity)
                Log = new WebhookLog(options.Capacity);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(options.Public ? IPAddress.Any : IPAddress.Loopback, options.Port, o => o.Protocols = HttpProtocols.Http1AndHttp2);
                // Oversized bodies are read and cut by the handler (and flagged), rather than rejected with 413.
                k.Limits.MaxRequestBodySize = null;
            });
            var app = builder.Build();
            app.Run(HandleAsync);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // A port in use, or cancellation: release the host so the next Start can bind.
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _app = app;
            var url = app.Urls.Select(u => new Uri(u.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost").Replace("127.0.0.1", "localhost")))
                .FirstOrDefault() ?? new Uri($"http://localhost:{options.Port}/");
            BaseUrl = url;
            Port = url.Port;
        }
        finally
        {
            _lifecycle.Release();
        }
        StateChanged?.Invoke();
    }

    public async Task<Uri> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (BaseUrl is { } running && IsRunning)
            return running;
        try
        {
            await StartAsync(new WebhookReceiverOptions(), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (IsRunning)
        {
            // Someone else started it in the meantime.
        }
        return BaseUrl!;
    }

    public async Task StopAsync()
    {
        WebApplication? app;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            app = _app;
            _app = null;
            BaseUrl = null;
            Port = null;
        }
        finally
        {
            _lifecycle.Release();
        }
        if (app is null)
            return;
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public Uri UrlFor(string path) => new(BaseUrl ?? new Uri($"http://localhost:{Port ?? Options.Port}/"), path.TrimStart('/'));

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var options = Options;

        // A plain browser visit to the root shows that the receiver is alive without recording a hook.
        if (HttpMethods.IsGet(request.Method) && request.Path.Value is null or "" or "/" && request.QueryString.HasValue is false
            && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            await response.WriteAsync($"<!doctype html><title>Dispatch webhook receiver</title><h1>Dispatch webhook receiver</h1>" +
                                      $"<p>Listening. {Log.Count} hook(s) recorded. Send callbacks to any path, e.g. <code>POST {BaseUrl}hooks/orders</code>.</p>",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var (body, bytes, truncated) = await ReadBodyAsync(request, options.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
        var hook = new ReceivedWebhook
        {
            Method = request.Method.ToUpperInvariant(),
            Path = request.Path.HasValue ? request.Path.Value! : "/",
            Query = request.QueryString.HasValue ? request.QueryString.Value!.TrimStart('?') : "",
            Headers = request.Headers.Select(h => new KeyValueItem(h.Key, h.Value.ToString())).ToList(),
            Body = body,
            BodyBytes = bytes,
            Truncated = truncated,
            ContentType = request.ContentType,
            RemoteAddress = context.Connection.RemoteIpAddress is { } ip ? $"{ip}:{context.Connection.RemotePort}" : ""
        };
        Log.Add(hook);

        if (options.ResponseDelayMs > 0)
            await Task.Delay(options.ResponseDelayMs, context.RequestAborted).ConfigureAwait(false);
        response.StatusCode = options.ResponseStatus;
        response.Headers["Dispatch-Webhook-Id"] = hook.Id.ToString("N");
        if (options.ResponseBody.Length > 0 && !HttpMethods.IsHead(request.Method))
        {
            response.ContentType = options.ResponseContentType;
            await response.WriteAsync(options.ResponseBody, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task<(string Body, long Bytes, bool Truncated)> ReadBodyAsync(HttpRequest request, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        long total = 0;
        var truncated = false;
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (buffer.Length < max)
                buffer.Write(chunk, 0, (int)Math.Min(read, max - buffer.Length));
            else
                truncated = true;
        }
        if (buffer.Length == 0)
            return ("", total, truncated);
        var encoding = Encoding.UTF8;
        try
        {
            if (Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var media) && media.Encoding is { } e)
                encoding = e;
        }
        catch (ArgumentException)
        {
            // Unknown charset: UTF-8 is the best guess.
        }
        return (encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), total, truncated || total > buffer.Length);
    }
}
