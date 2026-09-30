using System.Diagnostics;
using Dispatch.Application.Requests;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Running;

public sealed class RunOptions
{
    public string Name { get; init; } = "Collection run";
    public required IReadOnlyList<ApiRequest> Requests { get; init; }
    public ApiEnvironment? Environment { get; init; }
    public IReadOnlyList<KeyValueItem> CollectionVariables { get; init; } = [];
    public string? CollectionSpec { get; init; }
    public IReadOnlyDictionary<string, string>? Globals { get; init; }

    /// <summary>Ignored when <see cref="Data"/> has rows: then there is one iteration per row.</summary>
    public int Iterations { get; init; } = 1;
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Data { get; init; } = [];
    public int DelayMs { get; init; }
    public bool StopOnFailure { get; init; }
    public bool RecordHistory { get; init; }
}

public sealed record RequestRunResult(int Iteration, ApiRequest Request, ApiResponse Response)
{
    /// <summary>With tests, a request passes when it got a response and every test passed; without, when it succeeded.</summary>
    public bool Passed => Response.HasResponse && (Response.TestResults.Count > 0 ? Response.AllTestsPassed : Response.IsSuccess);

    public string Name => Request.Folder.Length > 0 ? $"{Request.Folder}/{Request.Name}" : Request.Name;
}

public sealed class RunReport
{
    public string Name { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Duration { get; set; }
    public int Iterations { get; set; }
    public List<RequestRunResult> Results { get; } = [];
    public bool Stopped { get; set; }

    /// <summary>Environment variables changed during the run (extraction rules, scripts).</summary>
    public Dictionary<string, string> EnvironmentUpdates { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> GlobalUpdates { get; } = new(StringComparer.Ordinal);

    public int TotalRequests => Results.Count;
    public int FailedRequests => Results.Count(r => !r.Passed);
    public int TotalTests => Results.Sum(r => r.Response.TestResults.Count);
    public int FailedTests => Results.Sum(r => r.Response.TestResults.Count(t => !t.Passed));
    public bool Passed => FailedRequests == 0;

    public TimeSpan AverageResponseTime =>
        Results.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Results.Average(r => r.Response.Elapsed.TotalMilliseconds));
}

/// <summary>
/// Runs a list of requests in order, for a number of iterations (or one per data row), sharing variables so values
/// extracted by one request feed the next.
/// </summary>
public sealed class CollectionRunner(IRequestSender sender)
{
    public async Task<RunReport> RunAsync(RunOptions options, IProgress<RequestRunResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new RunReport { Name = options.Name, StartedAt = DateTimeOffset.Now };
        var stopwatch = Stopwatch.StartNew();
        var variables = VariableContext.For(options.Environment, options.CollectionVariables, globals: options.Globals);
        var iterations = options.Data.Count > 0 ? options.Data.Count : Math.Max(1, options.Iterations);

        for (var iteration = 0; iteration < iterations && !cancellationToken.IsCancellationRequested; iteration++)
        {
            variables.Iteration = iteration;
            variables.Data.Clear();
            if (options.Data.Count > 0)
                foreach (var (k, v) in options.Data[iteration])
                    variables.Data[k] = v;
            report.Iterations = iteration + 1;

            foreach (var request in options.Requests)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                ApiResponse response;
                try
                {
                    response = await sender.SendAsync(request, new SendOptions
                    {
                        Variables = variables,
                        CollectionSpec = options.CollectionSpec,
                        RecordHistory = options.RecordHistory
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var result = new RequestRunResult(iteration, request, response);
                report.Results.Add(result);
                progress?.Report(result);

                if (options.StopOnFailure && !result.Passed)
                {
                    report.Stopped = true;
                    return Finish();
                }
                if (options.DelayMs > 0)
                {
                    try
                    {
                        await Task.Delay(options.DelayMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        report.Stopped = cancellationToken.IsCancellationRequested;
        return Finish();

        RunReport Finish()
        {
            report.Duration = stopwatch.Elapsed;
            foreach (var (k, v) in variables.EnvironmentUpdates)
                report.EnvironmentUpdates[k] = v;
            foreach (var (k, v) in variables.GlobalUpdates)
                report.GlobalUpdates[k] = v;
            return report;
        }
    }
}
