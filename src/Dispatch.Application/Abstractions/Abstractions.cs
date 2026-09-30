using System.Threading.Channels;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Abstractions;

/// <summary>Sends a fully built HTTP request and captures the response.</summary>
public interface IRequestExecutor
{
    Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, RequestSettings settings, CancellationToken cancellationToken);
}

/// <summary>Everything an executor needs besides the (already variable-resolved) request.</summary>
public sealed class ExecutionContext
{
    public VariableContext Variables { get; init; } = new();

    /// <summary>Live feed of streaming messages, so the UI can show them while the connection is open.</summary>
    public IProgress<StreamMessage>? Progress { get; init; }

    /// <summary>Messages typed by the user while an interactive session (WebSocket, gRPC stream, ...) is open.</summary>
    public ChannelReader<string>? Outgoing { get; init; }

    /// <summary>
    /// True in the UI: streaming sessions stay open until the user disconnects (or the server closes) instead of
    /// stopping after <c>ListenSeconds</c>.
    /// </summary>
    public bool Interactive { get; init; }

    public void Report(StreamMessage message) => Progress?.Report(message);
}

/// <summary>Executes requests of one or more <see cref="RequestKind"/>s. Must not throw for network / protocol errors.</summary>
public interface IProtocolExecutor
{
    IReadOnlyCollection<RequestKind> Kinds { get; }
    Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>Runs Postman-style JavaScript before and after a request.</summary>
public interface IScriptRunner
{
    /// <summary>Runs a pre-request script. It may change <paramref name="request"/> and variables.</summary>
    Task<ScriptResult> RunPreRequestAsync(string script, ApiRequest request, VariableContext variables, CancellationToken ct);

    /// <summary>Runs a test script against a response.</summary>
    Task<ScriptResult> RunTestsAsync(string script, ApiRequest request, ApiResponse response, VariableContext variables,
        CancellationToken ct);
}

public sealed record ScriptResult(IReadOnlyList<TestResult> Tests, IReadOnlyList<string> Log, string? Error = null)
{
    /// <summary>HTML rendered by <c>pm.visualizer.set(template, data)</c>.</summary>
    public string? Visualization { get; init; }

    public static readonly ScriptResult Empty = new([], []);
}

/// <summary>Turns OAuth 2.0 settings into an access token (fetching or refreshing it as needed).</summary>
public interface IOAuth2TokenProvider
{
    /// <summary>Returns the token, updating the cached token fields on <paramref name="auth"/>.</summary>
    Task<string> GetAccessTokenAsync(AuthSettings auth, CancellationToken ct);
}

/// <summary>Validates a response against an OpenAPI contract.</summary>
public interface IContractValidator
{
    Task<IReadOnlyList<string>> ValidateAsync(string specLocation, ApiRequest request, ApiResponse response,
        CancellationToken ct);
}

/// <summary>Cookie store shared by HTTP requests that opt in.</summary>
public interface ICookieJar
{
    string? GetCookieHeader(Uri uri);
    void Store(Uri uri, IEnumerable<string> setCookieHeaders);
    IReadOnlyList<CookieInfo> GetAll();
    void Delete(string domain, string path, string name);
    void Clear();
    event EventHandler? Changed;
}

public sealed record CookieInfo(string Domain, string Path, string Name, string Value, DateTimeOffset? Expires,
    bool Secure, bool HttpOnly);

public interface ICollectionRepository
{
    Task<IReadOnlyList<RequestCollection>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(RequestCollection collection, CancellationToken ct = default);
    Task RenameAsync(Guid collectionId, string name, CancellationToken ct = default);

    /// <summary>Updates name, description, variables and spec location (not the requests).</summary>
    Task UpdateAsync(RequestCollection collection, CancellationToken ct = default);
    Task DeleteAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>Inserts or updates a request. The request must belong to a collection.</summary>
    Task SaveRequestAsync(ApiRequest request, CancellationToken ct = default);
    Task DeleteRequestAsync(Guid requestId, CancellationToken ct = default);
}

public interface IEnvironmentRepository
{
    Task<IReadOnlyList<ApiEnvironment>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(ApiEnvironment environment, CancellationToken ct = default);
    Task DeleteAsync(Guid environmentId, CancellationToken ct = default);
}

public interface IHistoryRepository
{
    Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int take, CancellationToken ct = default);
    Task AddAsync(HistoryEntry entry, CancellationToken ct = default);
    Task DeleteAsync(Guid entryId, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Small key/value store for app preferences (active environment, theme, ...).</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

/// <summary>Encrypts secret variable values at rest using the OS facility where available.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

public static class SettingKeys
{
    public const string ActiveEnvironmentId = "activeEnvironmentId";
    public const string Theme = "theme";
    public const string Cookies = "cookies";
    public const string Globals = "globals";
}
