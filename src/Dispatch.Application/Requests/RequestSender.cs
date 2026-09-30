using System.Diagnostics;
using System.Threading.Channels;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Testing;
using Dispatch.Application.Variables;
using Dispatch.Domain;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Application.Requests;

public sealed class SendOptions
{
    public ApiEnvironment? Environment { get; init; }

    /// <summary>Use these variables (e.g. a collection run's shared context) instead of building them from the environment.</summary>
    public VariableContext? Variables { get; init; }
    public IReadOnlyList<KeyValueItem>? CollectionVariables { get; init; }

    /// <summary>OpenAPI document of the owning collection, for Contract assertions.</summary>
    public string? CollectionSpec { get; init; }
    public IProgress<StreamMessage>? Progress { get; init; }
    public ChannelReader<string>? Outgoing { get; init; }
    public bool Interactive { get; init; }
    public bool RecordHistory { get; init; } = true;
    public bool RunScripts { get; init; } = true;

    /// <summary>Whether snapshot assertions record missing snapshots, overwrite them, or only compare.</summary>
    public SnapshotMode Snapshots { get; init; } = SnapshotMode.RecordMissing;
}

public interface IRequestSender
{
    /// <summary>Resolves variables, sends the request and records it in history. Never throws for request/network errors.</summary>
    Task<ApiResponse> SendAsync(ApiRequest request, ApiEnvironment? environment, CancellationToken cancellationToken);

    /// <summary>
    /// Full pipeline: pre-request script, variable resolution, auth, protocol execution, extraction rules, assertions,
    /// test script, history. Never throws for request / network errors.
    /// </summary>
    Task<ApiResponse> SendAsync(ApiRequest request, SendOptions options, CancellationToken cancellationToken);
}

/// <summary>Variables that live for the app session: runtime values from extraction/scripts, and globals.</summary>
public sealed class SessionVariables
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Runtime
    {
        get { lock (_gate) return new Dictionary<string, string>(_runtime); }
    }

    public IReadOnlyDictionary<string, string> Globals
    {
        get { lock (_gate) return new Dictionary<string, string>(_globals); }
    }

    public void SetRuntime(IEnumerable<KeyValuePair<string, string>> values)
    {
        lock (_gate)
            foreach (var (k, v) in values)
                _runtime[k] = v;
    }

    public void SetGlobals(IEnumerable<KeyValuePair<string, string>> values)
    {
        lock (_gate)
            foreach (var (k, v) in values)
                _globals[k] = v;
    }

    public void ClearRuntime()
    {
        lock (_gate)
            _runtime.Clear();
    }
}

public sealed class RequestSender : IRequestSender
{
    private readonly IReadOnlyDictionary<RequestKind, IProtocolExecutor> _executors;
    private readonly IHistoryRepository _history;
    private readonly IScriptRunner? _scripts;
    private readonly IOAuth2TokenProvider? _oauth;
    private readonly AssertionEvaluator _assertions;
    private readonly SessionVariables _session;

    public RequestSender(
        IEnumerable<IProtocolExecutor> executors,
        IHistoryRepository history,
        SessionVariables? session = null,
        IScriptRunner? scripts = null,
        IOAuth2TokenProvider? oauth = null,
        IContractValidator? contracts = null)
    {
        var map = new Dictionary<RequestKind, IProtocolExecutor>();
        foreach (var executor in executors)
            foreach (var kind in executor.Kinds)
                map[kind] = executor;
        _executors = map;
        _history = history;
        _session = session ?? new SessionVariables();
        _scripts = scripts;
        _oauth = oauth;
        _assertions = new AssertionEvaluator(contracts);
    }

    public Task<ApiResponse> SendAsync(ApiRequest request, ApiEnvironment? environment, CancellationToken cancellationToken) =>
        SendAsync(request, new SendOptions { Environment = environment }, cancellationToken);

    public async Task<ApiResponse> SendAsync(ApiRequest request, SendOptions options, CancellationToken cancellationToken)
    {
        var variables = options.Variables
                        ?? VariableContext.For(options.Environment, options.CollectionVariables, _session.Runtime, _session.Globals);
        var log = new List<string>();
        var tests = new List<TestResult>();
        var working = request.Clone();

        // 1. Pre-request script: may change the request and variables.
        if (options.RunScripts && _scripts is not null && !string.IsNullOrWhiteSpace(working.PreRequestScript))
        {
            var pre = await _scripts.RunPreRequestAsync(working.PreRequestScript, working, variables, cancellationToken)
                .ConfigureAwait(false);
            log.AddRange(pre.Log);
            if (pre.Error is not null)
                return Finish(ApiResponse.Failed($"Pre-request script error: {pre.Error}", TimeSpan.Zero, kind: working.Kind),
                    variables, tests, log, options);
        }

        // 2. Variables, then auth that needs a network round-trip.
        var resolved = RequestResolver.Resolve(working, variables.Merged());
        AuthSettings? refreshedAuth = null;
        if (resolved.Auth.Mode == AuthMode.OAuth2 && _oauth is not null)
        {
            try
            {
                var token = await _oauth.GetAccessTokenAsync(resolved.Auth, cancellationToken).ConfigureAwait(false);
                refreshedAuth = resolved.Auth.Clone();
                resolved.Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = token };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Finish(ApiResponse.Failed($"OAuth 2.0: {ex.Message}", TimeSpan.Zero, kind: working.Kind),
                    variables, tests, log, options);
            }
        }

        // 3. Execute.
        if (!_executors.TryGetValue(resolved.Kind, out var executor))
            return Finish(ApiResponse.Failed($"{resolved.Kind} requests are not supported.", TimeSpan.Zero, kind: resolved.Kind),
                variables, tests, log, options);

        var context = new ExecutionContext
        {
            Variables = variables,
            Progress = options.Progress,
            Outgoing = options.Outgoing,
            Interactive = options.Interactive
        };

        ApiResponse response;
        try
        {
            response = await executor.ExecuteAsync(resolved, context, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestBuildException ex)
        {
            // Configuration errors are shown in the response pane and not recorded in history.
            return Finish(ApiResponse.Failed(ex.Message, TimeSpan.Zero, kind: resolved.Kind), variables, tests, log, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = ApiResponse.Failed(ex.Message, TimeSpan.Zero, resolved.Url, resolved.Kind);
        }
        response.RefreshedAuth = refreshedAuth;

        // 4. Extraction rules, then assertions (which may reference the extracted values).
        var (extracted, extractFailures) = AssertionEvaluator.Extract(resolved, response, variables.Resolve);
        foreach (var (name, (value, scope)) in extracted)
            variables.Set(name, value, scope);
        tests.AddRange(extractFailures);

        if (response.HasResponse)
            response.SnapshotUpdates = RecordSnapshots(resolved, response, options.Snapshots);

        tests.AddRange(await _assertions.EvaluateAsync(resolved, response, variables.Resolve, options.CollectionSpec,
            cancellationToken).ConfigureAwait(false));

        // 5. Test script.
        if (options.RunScripts && _scripts is not null && !string.IsNullOrWhiteSpace(working.TestScript))
        {
            var post = await _scripts.RunTestsAsync(working.TestScript, resolved, response, variables, cancellationToken)
                .ConfigureAwait(false);
            tests.AddRange(post.Tests);
            log.AddRange(post.Log);
            response.Visualization = post.Visualization;
            if (post.Error is not null)
                tests.Add(new TestResult("Test script", false, post.Error));
        }

        if (options.RecordHistory && !cancellationToken.IsCancellationRequested)
            await RecordHistoryAsync(request, response).ConfigureAwait(false);

        return Finish(response, variables, tests, log, options);
    }

    /// <summary>Stores the current body on snapshot assertions that need (re)recording; returns what changed.</summary>
    private static Dictionary<int, string> RecordSnapshots(ApiRequest resolved, ApiResponse response, SnapshotMode mode)
    {
        var updates = new Dictionary<int, string>();
        if (mode == SnapshotMode.Verify)
            return updates;
        for (var i = 0; i < resolved.Assertions.Count; i++)
        {
            var assertion = resolved.Assertions[i];
            if (!assertion.Enabled || assertion.Source != ValueSource.Snapshot)
                continue;
            if (mode == SnapshotMode.RecordMissing && assertion.Expected.Length > 0)
                continue;
            var snapshot = Snapshots.Capture(response);
            if (snapshot == assertion.Expected)
                continue;
            assertion.Expected = snapshot;
            updates[i] = snapshot;
        }
        return updates;
    }

    private ApiResponse Finish(ApiResponse response, VariableContext variables, List<TestResult> tests, List<string> log,
        SendOptions options)
    {
        response.TestResults = tests;
        response.ScriptLog = log;

        var updates = new Dictionary<string, string>(variables.Runtime, StringComparer.Ordinal);
        foreach (var (k, v) in variables.EnvironmentUpdates)
            updates[k] = v;
        response.VariableUpdates = updates;
        response.EnvironmentUpdates = new Dictionary<string, string>(variables.EnvironmentUpdates, StringComparer.Ordinal);

        // Outside a collection run, runtime values and globals persist for the rest of the session.
        if (options.Variables is null)
        {
            _session.SetRuntime(variables.Runtime);
            _session.SetGlobals(variables.GlobalUpdates);
        }

        // Environment updates are applied to the in-memory environment so the next send sees them;
        // the UI persists them.
        if (options.Environment is not null)
            foreach (var (k, v) in variables.EnvironmentUpdates)
                options.Environment.SetVariable(k, v);

        return response;
    }

    private async Task RecordHistoryAsync(ApiRequest request, ApiResponse response)
    {
        try
        {
            await _history.AddAsync(new HistoryEntry
            {
                Kind = request.Kind,
                Method = request.Method,
                Url = request.Url,
                StatusCode = response.HasResponse ? response.StatusCode : null,
                ElapsedMs = response.Elapsed.TotalMilliseconds,
                Request = request.Clone(newIdentity: true),
                Response = ResponseSnapshot.From(response)
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // History is best-effort; a storage failure must never hide the response from the user.
            Debug.WriteLine($"Failed to record history: {ex}");
        }
    }
}
