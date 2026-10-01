using System.Text.Json;
using Dispatch.Application.Formatting;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Capture;

/// <summary>Turns captured traffic into Dispatch requests and HAR, with a host/path filter for noise.</summary>
public static class CaptureConverter
{
    private static readonly string[] IgnoredRequestHeaders =
        ["host", "content-length", "connection", "proxy-connection", "accept-encoding", "transfer-encoding"];

    public static ApiRequest ToRequest(CapturedExchange exchange)
    {
        var request = new ApiRequest
        {
            Name = $"{exchange.Method} {Shorten(exchange.Path)}",
            Method = Enum.TryParse<HttpVerb>(exchange.Method, ignoreCase: true, out var verb) ? verb : HttpVerb.Get,
            Url = QueryString.WithParams(StripQuery(exchange.Url), QueryString.Parse(exchange.Url)),
            QueryParams = QueryString.Parse(exchange.Url),
            Headers = exchange.RequestHeaders
                .Where(h => !IgnoredRequestHeaders.Contains(h.Key.ToLowerInvariant()))
                .Select(h => h.Clone()).ToList()
        };

        if (exchange.RequestBody.Length > 0)
        {
            var type = exchange.RequestContentType ?? "";
            request.Body = type.Contains("json") ? new RequestBody { Mode = BodyMode.Json, Content = exchange.RequestBody }
                : type.Contains("xml") ? new RequestBody { Mode = BodyMode.Xml, Content = exchange.RequestBody }
                : type.Contains("x-www-form-urlencoded") ? new RequestBody { Mode = BodyMode.FormUrlEncoded, FormFields = QueryString.Parse("?" + exchange.RequestBody) }
                : new RequestBody { Mode = BodyMode.Text, Content = exchange.RequestBody };
        }

        if (exchange.StatusCode > 0)
            request.Examples.Add(new ResponseExample
            {
                Name = $"{exchange.StatusCode} captured",
                StatusCode = exchange.StatusCode,
                ContentType = exchange.ResponseContentType ?? "application/json",
                Body = exchange.ResponseBody
            });
        return request;
    }

    private static string StripQuery(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q];
    }

    private static string Shorten(string path) => path.Length <= 48 ? path : "…" + path[^47..];

    /// <summary>HTTP Archive (HAR 1.2) of the captured exchanges, importable by browsers and other tools.</summary>
    public static string ToHar(IEnumerable<CapturedExchange> exchanges)
    {
        var entries = exchanges.Select(e =>
        {
            var query = QueryString.Parse(e.Url);
            return new
            {
                startedDateTime = e.Timestamp.ToString("o"),
                time = e.ElapsedMs,
                request = new
                {
                    method = e.Method,
                    url = e.Url,
                    httpVersion = "HTTP/1.1",
                    headers = e.RequestHeaders.Select(h => new { name = h.Key, value = h.Value }),
                    queryString = query.Select(q => new { name = q.Key, value = q.Value }),
                    headersSize = -1,
                    bodySize = e.RequestBody.Length,
                    postData = e.RequestBody.Length > 0
                        ? new { mimeType = e.RequestContentType ?? "text/plain", text = e.RequestBody }
                        : null
                },
                response = new
                {
                    status = e.StatusCode,
                    statusText = e.ReasonPhrase,
                    httpVersion = "HTTP/1.1",
                    headers = e.ResponseHeaders.Select(h => new { name = h.Key, value = h.Value }),
                    content = new { size = e.ResponseSize, mimeType = e.ResponseContentType ?? "", text = e.ResponseBody },
                    redirectURL = e.ResponseHeaders.FirstOrDefault(h => h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))?.Value ?? "",
                    headersSize = -1,
                    bodySize = e.ResponseSize
                },
                cache = new { },
                timings = new { send = 0, wait = e.ElapsedMs, receive = 0 }
            };
        });

        return JsonSerializer.Serialize(new
        {
            log = new
            {
                version = "1.2",
                creator = new { name = "Dispatch", version = "0.1.0" },
                entries
            }
        }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}
