using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Domain;

namespace Dispatch.Application.Flows;

/// <summary>A response as recorded during a flow run (enough to serve it again without the network).</summary>
public sealed class RecordedResponse
{
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string Body { get; set; } = string.Empty;
    public List<KeyValueItem> Headers { get; set; } = [];

    /// <summary>Set when there was no response (timeout, connection refused…).</summary>
    public string? Error { get; set; }
    public double ElapsedMs { get; set; }
    public string? EffectiveUrl { get; set; }

    public static RecordedResponse From(ApiResponse r) => new()
    {
        StatusCode = r.StatusCode,
        ReasonPhrase = r.ReasonPhrase,
        ContentType = r.ContentType,
        Body = r.Body,
        Headers = r.Headers.Select(h => new KeyValueItem(h.Name, h.Value)).ToList(),
        Error = r.Error,
        ElapsedMs = r.Elapsed.TotalMilliseconds,
        EffectiveUrl = r.EffectiveUrl
    };

    /// <summary>A fresh response object (the pipeline fills in tests and variables on it).</summary>
    public ApiResponse ToResponse() => Error is not null
        ? ApiResponse.Failed(Error, TimeSpan.FromMilliseconds(ElapsedMs), EffectiveUrl)
        : new ApiResponse
        {
            StatusCode = StatusCode,
            ReasonPhrase = ReasonPhrase,
            ContentType = ContentType,
            Body = Body,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(Body),
            Headers = Headers.Select(h => new ResponseHeader(h.Key, h.Value)).ToList(),
            Elapsed = TimeSpan.FromMilliseconds(ElapsedMs),
            EffectiveUrl = EffectiveUrl
        };

    public RecordedResponse Clone() => new()
    {
        StatusCode = StatusCode, ReasonPhrase = ReasonPhrase, ContentType = ContentType, Body = Body,
        Headers = Headers.Select(h => h.Clone()).ToList(), Error = Error, ElapsedMs = ElapsedMs, EffectiveUrl = EffectiveUrl
    };
}

/// <summary>
/// One request sent during a flow run. <see cref="Ordinal"/> numbers the requests of the run in order (#1, #2, …);
/// <see cref="Occurrence"/> counts how often the same step ran (loops), which is how replays find it again.
/// </summary>
public sealed record RecordedExchange(int Ordinal, Guid StepId, int Occurrence, string StepName, RecordedResponse Response);

/// <summary>How a step ended in a run, for comparing a replay with the original.</summary>
public sealed record RecordedStep(string Name, int Depth, bool Ok, string Detail);

/// <summary>Everything a flow run sent and received, so it can be replayed and forked later.</summary>
public sealed class FlowRecording
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FlowName { get; set; } = string.Empty;
    public Guid FlowId { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.Now;
    public bool Passed { get; set; }
    public List<RecordedExchange> Exchanges { get; set; } = [];
    public List<RecordedStep> Steps { get; set; } = [];

    public RecordedExchange? Find(int ordinal) => Exchanges.FirstOrDefault(e => e.Ordinal == ordinal);

    public RecordedExchange? Find(Guid stepId, int occurrence) =>
        Exchanges.FirstOrDefault(e => e.StepId == stepId && e.Occurrence == occurrence);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static FlowRecording FromJson(string json) =>
        JsonSerializer.Deserialize<FlowRecording>(json, Options) ?? throw new FormatException("Not a flow recording.");
}

/// <summary>What happens to the requests after the fork point.</summary>
public enum AfterFork
{
    /// <summary>Send them for real.</summary>
    Live,

    /// <summary>Serve them from the recording too (offline what-if; steps that didn't run originally fail).</summary>
    Recorded
}

/// <summary>
/// Replays a recorded run and changes one response: the flow runs again from the start, requests before the fork are
/// answered from the recording (delays skipped), the fork request gets <see cref="Replacement"/>, and later requests run
/// live or from the recording.
/// </summary>
public sealed class FlowFork
{
    public required FlowRecording Recording { get; init; }

    /// <summary>The request (#ordinal in the original run) whose response is replaced.</summary>
    public required int ForkOrdinal { get; init; }

    /// <summary>The response to use instead; null sends the fork request live.</summary>
    public RecordedResponse? Replacement { get; init; }
    public AfterFork After { get; init; } = AfterFork.Live;
}

/// <summary>Where a request's response came from during a run.</summary>
public enum ExchangeOrigin
{
    Live,
    Recorded,
    Edited
}

/// <summary>Ready-made replacements for the fork editor.</summary>
public static class ForkPresets
{
    public static RecordedResponse Status(RecordedResponse original, int status, string? reason = null, string? body = null)
    {
        var copy = original.Clone();
        copy.Error = null;
        copy.StatusCode = status;
        copy.ReasonPhrase = string.IsNullOrWhiteSpace(reason) ? ReasonFor(status) : reason;
        if (body is not null)
            copy.Body = body;
        return copy;
    }

    /// <summary>The standard reason phrase: 503 → "Service Unavailable".</summary>
    public static string ReasonFor(int status) =>
        Enum.IsDefined(typeof(System.Net.HttpStatusCode), status)
            ? System.Text.RegularExpressions.Regex.Replace(((System.Net.HttpStatusCode)status).ToString(), "(?<=[a-z])(?=[A-Z])", " ")
            : "";

    public static RecordedResponse NoResponse(RecordedResponse original, string error = "Simulated: the request timed out")
    {
        var copy = original.Clone();
        copy.Error = error;
        return copy;
    }

    /// <summary>The same JSON with every array emptied (an "empty list" variant of the response).</summary>
    public static RecordedResponse EmptyArrays(RecordedResponse original)
    {
        var copy = original.Clone();
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(original.Body) is { } root)
            {
                Empty(root);
                copy.Body = root.ToJsonString(new JsonSerializerOptions { WriteIndented = original.Body.Contains('\n') });
            }
        }
        catch (JsonException)
        {
        }
        return copy;

        static void Empty(System.Text.Json.Nodes.JsonNode node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonArray array:
                    array.Clear();
                    break;
                case System.Text.Json.Nodes.JsonObject obj:
                    foreach (var (_, value) in obj.ToList())
                        if (value is not null)
                            Empty(value);
                    break;
            }
        }
    }
}

/// <summary>A difference between how a step ended in the original run and in a replay.</summary>
public sealed record FlowDivergence(string Step, string Before, string After)
{
    public override string ToString() => $"{Step}: {Before} → {After}";
}

public static class FlowRunDiff
{
    /// <summary>Steps whose outcome changed, steps that only ran in one of the two, and the overall verdict.</summary>
    public static IReadOnlyList<FlowDivergence> Compare(FlowRecording original, FlowResult replay)
    {
        static List<(string Key, RecordedStep Step)> Keyed(IEnumerable<RecordedStep> steps)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            return steps.Select(s =>
            {
                seen[s.Name] = seen.GetValueOrDefault(s.Name) + 1;
                return (seen[s.Name] == 1 ? s.Name : $"{s.Name} (#{seen[s.Name]})", s);
            }).ToList();
        }
        var before = Keyed(original.Steps);
        var after = Keyed(Outcomes(replay));
        var list = new List<FlowDivergence>();
        if (original.Passed != replay.Passed)
            list.Add(new FlowDivergence("Flow", original.Passed ? "passed" : "failed", replay.Passed ? "passed" : "failed"));
        foreach (var (key, step) in after)
        {
            var match = before.FirstOrDefault(b => b.Key == key);
            if (match.Step is null)
                list.Add(new FlowDivergence(key, "did not run", Describe(step)));
            else if (match.Step.Ok != step.Ok || StatusOf(match.Step.Detail) != StatusOf(step.Detail))
                list.Add(new FlowDivergence(key, Describe(match.Step), Describe(step)));
        }
        foreach (var (key, step) in before.Where(b => after.All(a => a.Key != b.Key)))
            list.Add(new FlowDivergence(key, Describe(step), "did not run"));
        return list;
    }

    /// <summary>The finished / failed step lines of a run, as recorded for later comparison.</summary>
    public static List<RecordedStep> Outcomes(FlowResult result) =>
        result.Events.Where(e => e.Kind is FlowEventKind.StepFinished or FlowEventKind.Failed)
            .Select(e => new RecordedStep(e.StepName, e.Depth, e.Ok, e.Detail)).ToList();

    private static string Describe(RecordedStep step) => (step.Ok ? "passed" : "failed") + (StatusOf(step.Detail) is { } s ? $" ({s})" : "");

    /// <summary>The leading "200 OK" / "error: …" part of a request step's detail.</summary>
    private static string? StatusOf(string detail)
    {
        var text = detail.StartsWith("✎ ", StringComparison.Ordinal) || detail.StartsWith("↺ ", StringComparison.Ordinal) ? detail[2..] : detail;
        var cut = text.IndexOf(" · ", StringComparison.Ordinal);
        var head = (cut >= 0 ? text[..cut] : text).Trim();
        return head.Length > 0 && (char.IsDigit(head[0]) || head.StartsWith("error", StringComparison.Ordinal)) ? head : null;
    }
}
