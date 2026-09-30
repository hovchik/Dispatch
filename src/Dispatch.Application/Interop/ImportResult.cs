using Dispatch.Domain;

namespace Dispatch.Application.Interop;

public sealed class ImportResult
{
    public string Format { get; init; } = "";
    public List<RequestCollection> Collections { get; } = [];
    public List<ApiEnvironment> Environments { get; } = [];
    public List<string> Warnings { get; } = [];

    public int RequestCount => Collections.Sum(c => c.Requests.Count);

    public static ImportResult Single(string format, RequestCollection collection)
    {
        var result = new ImportResult { Format = format };
        result.Collections.Add(collection);
        return result;
    }
}
