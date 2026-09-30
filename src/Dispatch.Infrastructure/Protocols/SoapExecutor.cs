using System.Collections.Concurrent;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Application.Soap;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols;

/// <summary>Fetches WSDL documents (and their imports) from URLs or files, with caching.</summary>
public sealed class WsdlLoader(IHttpClientSource clients)
{
    private readonly ConcurrentDictionary<string, WsdlDocument> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WsdlDocument> LoadAsync(string location, RequestSettings settings, bool refresh, CancellationToken ct)
    {
        location = location.Trim();
        if (location.Length == 0)
            throw new ArgumentException("Enter the WSDL URL or file path.");
        if (!refresh && _cache.TryGetValue(location, out var cached))
            return cached;

        var doc = await WsdlDocument.LoadAsync(location, async (loc, token) =>
        {
            if (Uri.TryCreate(loc, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await clients.GetClient(settings).GetAsync(uri, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            var path = uri is { IsFile: true } ? uri.LocalPath : loc;
            return await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        _cache[location] = doc;
        return doc;
    }
}

/// <summary>
/// SOAP 1.1 / 1.2 over HTTP: the body is the envelope; sets SOAPAction / action, adds WS-Security headers, and turns
/// SOAP faults into failed responses with the fault code and reason.
/// </summary>
public sealed class SoapExecutor(HttpProtocolExecutor http) : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Soap];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var soap = request.Protocol.Soap;
        if (string.IsNullOrWhiteSpace(request.Body.Content))
            throw new RequestBuildException("Enter the SOAP envelope (or generate one from the WSDL).");

        string envelope;
        try
        {
            envelope = SoapEnvelope.ApplyWsSecurity(request.Body.Content, soap);
        }
        catch (FormatException ex)
        {
            throw new RequestBuildException(ex.Message);
        }

        var httpRequest = request.Clone();
        httpRequest.Kind = RequestKind.Http;
        httpRequest.Method = HttpVerb.Post;
        httpRequest.Body = new RequestBody { Mode = BodyMode.Xml, Content = envelope };
        httpRequest.Headers.RemoveAll(h => h.Key.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                                           || h.Key.Trim().Equals("SOAPAction", StringComparison.OrdinalIgnoreCase));

        var action = soap.Action.Trim();
        if (soap.Version == SoapVersion.Soap12)
        {
            httpRequest.Headers.Add(new KeyValueItem("Content-Type",
                "application/soap+xml; charset=utf-8" + (action.Length > 0 ? $"; action=\"{action}\"" : "")));
        }
        else
        {
            httpRequest.Headers.Add(new KeyValueItem("Content-Type", "text/xml; charset=utf-8"));
            httpRequest.Headers.Add(new KeyValueItem("SOAPAction", $"\"{action}\""));
        }

        var response = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var fault = response.HasResponse ? SoapEnvelope.ParseFault(response.Body) : null;

        return new ApiResponse
        {
            Kind = RequestKind.Soap,
            StatusCode = response.StatusCode,
            ReasonPhrase = fault is null ? response.ReasonPhrase : $"SOAP Fault · {fault.Code}: {fault.Reason}",
            Succeeded = fault is null ? null : false,
            Elapsed = response.Elapsed,
            SizeBytes = response.SizeBytes,
            ContentType = response.ContentType,
            Body = response.Body,
            IsBodyTruncated = response.IsBodyTruncated,
            Headers = response.Headers,
            Timings = response.Timings,
            RawRequest = response.RawRequest,
            EffectiveUrl = response.EffectiveUrl,
            Error = response.Error
        };
    }
}
