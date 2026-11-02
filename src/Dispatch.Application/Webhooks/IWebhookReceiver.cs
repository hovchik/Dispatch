namespace Dispatch.Application.Webhooks;

/// <summary>How the local webhook endpoint listens and answers.</summary>
public sealed class WebhookReceiverOptions
{
    /// <summary>Port used when nothing else is configured: <c>DISPATCH_WEBHOOK_PORT</c>, else 9000.</summary>
    public static int DefaultPort =>
        int.TryParse(Environment.GetEnvironmentVariable("DISPATCH_WEBHOOK_PORT"), out var port) && port is >= 0 and <= 65535 ? port : 9000;

    /// <summary>0 picks a free port.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Listen on all interfaces (for callbacks from other machines or a tunnel on another host).</summary>
    public bool Public { get; init; }

    /// <summary>Bodies larger than this are cut and flagged as truncated.</summary>
    public int MaxBodyBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>What every callback is answered with.</summary>
    public int ResponseStatus { get; init; } = 200;
    public string ResponseBody { get; init; } = """{"received":true}""";
    public string ResponseContentType { get; init; } = "application/json";

    /// <summary>Delay before answering, to test senders' timeouts and retries.</summary>
    public int ResponseDelayMs { get; init; }

    /// <summary>How many hooks are kept in memory.</summary>
    public int Capacity { get; init; } = 2000;
}

/// <summary>
/// A local HTTP endpoint that records every callback it receives (any method, any path). Started on demand by webhook
/// message checks, or explicitly from the viewer window and <c>dispatch webhooks</c>.
/// </summary>
public interface IWebhookReceiver
{
    WebhookLog Log { get; }
    bool IsRunning { get; }

    /// <summary>Where senders should deliver callbacks, e.g. <c>http://localhost:9000/</c>; null while stopped.</summary>
    Uri? BaseUrl { get; }

    /// <summary>The port actually bound (resolved when 0 was requested); null while stopped.</summary>
    int? Port { get; }
    WebhookReceiverOptions Options { get; }

    /// <summary>Raised whenever the receiver starts or stops.</summary>
    event Action? StateChanged;

    /// <summary>Starts listening. Throws when already running or the port is taken.</summary>
    Task StartAsync(WebhookReceiverOptions options, CancellationToken ct = default);

    /// <summary>Starts on the default port when not running yet; returns the base URL either way.</summary>
    Task<Uri> EnsureStartedAsync(CancellationToken ct = default);

    Task StopAsync();

    /// <summary>The full URL a callback to <paramref name="path"/> should be sent to.</summary>
    Uri UrlFor(string path);
}
