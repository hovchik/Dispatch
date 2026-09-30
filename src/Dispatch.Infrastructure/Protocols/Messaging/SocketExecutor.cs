using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols.Messaging;

/// <summary>
/// Raw TCP (optionally TLS) and UDP: sends a text/hex/base64 payload and shows what comes back, as text and hex.
/// TCP keeps reading until the server closes or goes quiet for the read timeout; UDP waits for datagrams.
/// </summary>
public sealed class SocketExecutor : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Tcp, RequestKind.Udp];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var s = request.Protocol.Socket;
        var udp = request.Kind == RequestKind.Udp;
        var address = BrokerAddress.Parse(request.Url, udp ? "udp" : "tcp", 0);
        if (address.Port <= 0)
            throw new RequestBuildException("The address needs a port, e.g. tcp://localhost:7000.");

        byte[] payload;
        try
        {
            payload = EncodePayload(s);
        }
        catch (FormatException ex)
        {
            throw new RequestBuildException(ex.Message);
        }

        using var log = new MessageLog(context, 0, 0, cancellationToken);
        var quiet = TimeSpan.FromSeconds(s.ReadTimeoutSeconds > 0 ? s.ReadTimeoutSeconds : 3);
        var received = new MemoryStream();

        try
        {
            if (udp)
                await UdpAsync(address, payload, quiet, received, log, context, cancellationToken).ConfigureAwait(false);
            else
                await TcpAsync(request, address, payload, quiet, received, log, context, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return ApiResponse.Failed($"{(udp ? "UDP" : "TCP")} error: {ex.Message}", log.Stopwatch.Elapsed, address.ToString(), request.Kind);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
        {
            return ApiResponse.Failed(ex.Message, log.Stopwatch.Elapsed, address.ToString(), request.Kind);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResponse.Failed("Connection timed out.", log.Stopwatch.Elapsed, address.ToString(), request.Kind);
        }

        var bytes = received.ToArray();
        return new ApiResponse
        {
            Kind = request.Kind,
            StatusCode = 0,
            ReasonPhrase = bytes.Length == 0 ? "Sent (no reply)" : $"{bytes.Length:N0} bytes received",
            Succeeded = true,
            Elapsed = log.Stopwatch.Elapsed,
            SizeBytes = bytes.Length,
            ContentType = "text/plain",
            Body = Encoding.UTF8.GetString(bytes),
            Messages = log.Messages,
            EffectiveUrl = address.ToString()
        };
    }

    private static async Task TcpAsync(ApiRequest request, BrokerAddress address, byte[] payload, TimeSpan quiet, MemoryStream received,
        MessageLog log, ExecutionContext context, CancellationToken ct)
    {
        using var client = new TcpClient { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(request.Settings.TimeoutMs > 0 ? request.Settings.TimeoutMs : 15_000);
            await client.ConnectAsync(address.Host, address.Port, connectTimeout.Token).ConfigureAwait(false);
        }
        log.Info($"Connected to {address.Host}:{address.Port}");

        Stream stream = client.GetStream();
        if (request.Protocol.Socket.UseTls || address.Scheme is "tls" or "ssl" or "tcps")
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions { TargetHost = address.Host };
            if (!request.Settings.VerifySsl)
                options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            if (!string.IsNullOrWhiteSpace(request.Settings.ClientCertificatePath))
                options.ClientCertificates = [Http.HttpClientPool.LoadCertificate(request.Settings)];
            await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
            log.Info($"TLS {ssl.SslProtocol} established");
            stream = ssl;
        }

        await using (stream)
        {
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
                log.Sent(Describe(payload));
            }

            var sending = context.Outgoing is null
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var text in context.Outgoing.ReadAllAsync(log.Token).ConfigureAwait(false))
                        {
                            var bytes = EncodePayload(request.Protocol.Socket, text);
                            await stream.WriteAsync(bytes, log.Token).ConfigureAwait(false);
                            log.Sent(Describe(bytes));
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                    }
                }, CancellationToken.None);

            var buffer = new byte[64 * 1024];
            while (!log.Token.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(log.Token);
                // Interactive sessions stay open; otherwise stop when the server goes quiet.
                if (!context.Interactive)
                    idle.CancelAfter(quiet);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    log.Info(log.Token.IsCancellationRequested ? "Closed" : $"No data for {quiet.TotalSeconds:0.#} s; closing");
                    break;
                }
                if (read == 0)
                {
                    log.Info("Server closed the connection");
                    break;
                }
                received.Write(buffer, 0, read);
                log.Received(Describe(buffer.AsSpan(0, read).ToArray()), $"{read} bytes", read);
            }
            log.Stop();
            await sending.ConfigureAwait(false);
        }
    }

    private static async Task UdpAsync(BrokerAddress address, byte[] payload, TimeSpan quiet, MemoryStream received, MessageLog log,
        ExecutionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(address.Host, ct).ConfigureAwait(false);
        var target = new IPEndPoint(addresses.First(), address.Port);
        using var client = new UdpClient(target.AddressFamily);
        client.Connect(target);

        await client.SendAsync(payload, ct).ConfigureAwait(false);
        log.Sent(Describe(payload), $"to {target}");

        while (!log.Token.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(log.Token);
            if (!context.Interactive)
                idle.CancelAfter(quiet);
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.Info($"No datagram for {quiet.TotalSeconds:0.#} s");
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                log.Failure("Port unreachable (ICMP)");
                break;
            }
            received.Write(result.Buffer);
            log.Received(Describe(result.Buffer), $"from {result.RemoteEndPoint}", result.Buffer.Length);
        }
    }

    internal static byte[] EncodePayload(SocketSettings s) => EncodePayload(s, s.Payload);

    internal static byte[] EncodePayload(SocketSettings s, string text)
    {
        switch (s.Encoding)
        {
            case PayloadEncoding.Hex:
                var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
                if (hex.Length % 2 != 0)
                    throw new FormatException("Hex payload must have an even number of digits.");
                return Convert.FromHexString(hex);
            case PayloadEncoding.Base64:
                return Convert.FromBase64String(text.Trim());
            default:
                var ending = s.LineEnding switch
                {
                    "\\n" or "LF" => "\n",
                    "\\r\\n" or "CRLF" => "\r\n",
                    _ => s.LineEnding
                };
                return Encoding.UTF8.GetBytes(text.Replace("\\r", "\r").Replace("\\n", "\n") + ending);
        }
    }

    /// <summary>Text when printable, otherwise a hex dump.</summary>
    internal static string Describe(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "(empty)";
        var printable = bytes.All(b => b >= 0x20 || b is 0x09 or 0x0A or 0x0D);
        return printable
            ? Encoding.UTF8.GetString(bytes)
            : $"hex: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 1024)))}{(bytes.Length > 1024 ? "…" : "")}";
    }
}
