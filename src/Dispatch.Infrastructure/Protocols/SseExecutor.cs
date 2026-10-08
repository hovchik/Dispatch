using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols;

/// <summary>Server-Sent Events: a long-lived GET whose <c>text/event-stream</c> body is split into events.</summary>
public sealed class SseExecutor(IRequestMessageBuilder builder, IHttpClientSource clients) : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Sse];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = request.Protocol.Stream;
        var httpRequest = request.Clone();
        httpRequest.Kind = RequestKind.Http;
        using var message = builder.Build(httpRequest, new Dictionary<string, string>());
        message.Headers.Remove("Accept");
        message.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        message.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

        using var log = new MessageLog(context, settings.ListenSeconds, settings.MaxMessages, cancellationToken);

        HttpResponseMessage response;
        try
        {
            // Inside the try: a missing client certificate or an invalid proxy URL is an error response, not a crash.
            var client = clients.GetClient(request.Settings);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(log.Token);
            connectTimeout.CancelAfter(request.Settings.TimeoutMs > 0 ? request.Settings.TimeoutMs : 30_000);
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResponse.Failed("Request cancelled.", log.Stopwatch.Elapsed, message.RequestUri?.ToString(), RequestKind.Sse);
        }
        catch (OperationCanceledException)
        {
            return ApiResponse.Failed("Timed out waiting for the event stream.", log.Stopwatch.Elapsed,
                message.RequestUri?.ToString(), RequestKind.Sse);
        }
        catch (HttpRequestException ex)
        {
            return ApiResponse.Failed(HttpRequestExecutor.Describe(ex), log.Stopwatch.Elapsed, message.RequestUri?.ToString(),
                RequestKind.Sse);
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UriFormatException
                                       or System.Security.Authentication.AuthenticationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            return ApiResponse.Failed(ex.Message, log.Stopwatch.Elapsed, message.RequestUri?.ToString(), RequestKind.Sse);
        }

        using (response)
        {
            var firstByte = log.Stopwatch.Elapsed;
            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(h => new ResponseHeader(h.Key, string.Join(", ", h.Value))).ToList();
            var body = string.Empty;

            if (!response.IsSuccessStatusCode)
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                log.Failure($"Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            else
            {
                log.Info("Connected; listening for events");
                log.Listening();
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(log.Token).ConfigureAwait(false);
                    await ReadEventsAsync(stream, log).ConfigureAwait(false);
                    log.Info("Stream ended by the server");
                }
                catch (OperationCanceledException)
                {
                    log.Info("Disconnected");
                }
                catch (IOException ex)
                {
                    log.Failure(ex.Message);
                }
            }

            return new ApiResponse
            {
                Kind = RequestKind.Sse,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? string.Empty,
                Elapsed = log.Stopwatch.Elapsed,
                SizeBytes = log.ReceivedBytes,
                ContentType = response.Content.Headers.ContentType?.MediaType,
                Body = body,
                Headers = headers,
                Messages = log.Messages,
                Timings = new ResponseTimings(FirstByte: firstByte),
                EffectiveUrl = message.RequestUri?.ToString()
            };
        }
    }

    /// <summary>Parses the event-stream format (https://html.spec.whatwg.org/multipage/server-sent-events.html).</summary>
    internal static async Task ReadEventsAsync(Stream stream, MessageLog log)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        string? eventName = null;
        string? id = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(log.Token).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var text = data.ToString(0, data.Length - 1); // drop the trailing \n
                    var label = eventName ?? "message";
                    if (id is not null)
                        label += $" #{id}";
                    log.Received(text, label);
                }
                data.Clear();
                eventName = null;
                continue;
            }
            if (line.StartsWith(':'))
                continue; // comment / keep-alive

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "data":
                    data.Append(value).Append('\n');
                    break;
                case "event":
                    eventName = value;
                    break;
                case "id":
                    id = value;
                    break;
            }
        }
    }
}
