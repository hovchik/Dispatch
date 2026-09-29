using System.Diagnostics;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;

namespace Dispatch.Application.Requests;

public interface IRequestSender
{
    /// <summary>Resolves variables, sends the request and records it in history. Never throws for request/network errors.</summary>
    Task<ApiResponse> SendAsync(ApiRequest request, ApiEnvironment? environment, CancellationToken cancellationToken);
}

public sealed class RequestSender(
    IRequestMessageBuilder builder,
    IRequestExecutor executor,
    IHistoryRepository history) : IRequestSender
{
    public async Task<ApiResponse> SendAsync(ApiRequest request, ApiEnvironment? environment, CancellationToken cancellationToken)
    {
        var variables = environment?.ToDictionary() ?? new Dictionary<string, string>();

        ApiResponse response;
        try
        {
            using var message = builder.Build(request, variables);
            response = await executor.ExecuteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestBuildException ex)
        {
            // Configuration errors are shown in the response pane and not recorded in history.
            return ApiResponse.Failed(ex.Message, TimeSpan.Zero);
        }

        if (!cancellationToken.IsCancellationRequested)
            await RecordHistoryAsync(request, response).ConfigureAwait(false);

        return response;
    }

    private async Task RecordHistoryAsync(ApiRequest request, ApiResponse response)
    {
        try
        {
            await history.AddAsync(new HistoryEntry
            {
                Method = request.Method,
                Url = request.Url,
                StatusCode = response.HasResponse ? response.StatusCode : null,
                ElapsedMs = response.Elapsed.TotalMilliseconds,
                Request = request.Clone(newIdentity: true)
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // History is best-effort; a storage failure must never hide the response from the user.
            Debug.WriteLine($"Failed to record history: {ex}");
        }
    }
}
