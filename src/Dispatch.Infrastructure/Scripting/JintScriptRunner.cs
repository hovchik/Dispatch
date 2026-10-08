using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Formatting;
using Dispatch.Application.Variables;
using Dispatch.Domain;
using Jint;
using Jint.Runtime;

namespace Dispatch.Infrastructure.Scripting;

/// <summary>
/// Runs pre-request and test scripts in a sandboxed JavaScript engine (Jint) with a Postman-compatible <c>pm</c> API:
/// <c>pm.environment / variables / globals / collectionVariables / iterationData</c>, <c>pm.request</c>,
/// <c>pm.response</c>, <c>pm.test</c>, <c>pm.expect</c> (chai-style) and <c>console</c>.
/// </summary>
public sealed class JintScriptRunner : IScriptRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Prepared<Acornima.Ast.Script> Prelude = Engine.PrepareScript(ScriptPrelude.Source);

    public Task<ScriptResult> RunPreRequestAsync(string script, ApiRequest request, VariableContext variables, CancellationToken ct) =>
        Task.Run(() => Run(script, request, null, variables, applyRequestChanges: true, ct), ct);

    public Task<ScriptResult> RunTestsAsync(string script, ApiRequest request, ApiResponse response, VariableContext variables,
        CancellationToken ct) =>
        Task.Run(() => Run(script, request, response, variables, applyRequestChanges: false, ct), ct);

    private static ScriptResult Run(string script, ApiRequest request, ApiResponse? response, VariableContext variables,
        bool applyRequestChanges, CancellationToken ct)
    {
        var tests = new List<TestResult>();
        var log = new List<string>();
        string? visualization = null;

        using var engine = new Engine(options => options
            .TimeoutInterval(Timeout)
            .LimitMemory(64 * 1024 * 1024)
            .LimitRecursion(256)
            .MaxStatements(5_000_000)
            .CancellationToken(ct)
            .Strict(false));

        engine.SetValue("__envGet", new Func<string, string?>(k => variables.Environment.GetValueOrDefault(k)));
        engine.SetValue("__envSet", new Action<string, string>((k, v) => variables.Set(k, v, VariableScope.Environment)));
        engine.SetValue("__envUnset", new Action<string>(variables.Unset));
        engine.SetValue("__varGet", new Func<string, string?>(variables.Get));
        engine.SetValue("__varSet", new Action<string, string>((k, v) => variables.Set(k, v, VariableScope.Runtime)));
        engine.SetValue("__varUnset", new Action<string>(k => variables.Runtime.Remove(k)));
        engine.SetValue("__globalGet", new Func<string, string?>(k => variables.Globals.GetValueOrDefault(k)));
        engine.SetValue("__globalSet", new Action<string, string>(variables.SetGlobal));
        engine.SetValue("__dataGet", new Func<string, string?>(k => variables.Data.GetValueOrDefault(k)));
        engine.SetValue("__resolve", new Func<string, string>(variables.Resolve));
        engine.SetValue("__variablesJson", new Func<string>(() => JsonSerializer.Serialize(variables.Merged())));
        engine.SetValue("__iteration", new Func<int>(() => variables.Iteration));
        engine.SetValue("__requestJson", new Func<string>(() => RequestJson(request)));
        engine.SetValue("__responseJson", new Func<string?>(() => response is null ? null : ResponseJson(response)));
        engine.SetValue("__test", new Action<string, bool, string>((name, passed, message) =>
            tests.Add(new TestResult(name, passed, passed ? null : message))));
        engine.SetValue("__log", new Action<string, string>((level, text) =>
            log.Add(level == "log" ? text : $"[{level}] {text}")));
        engine.SetValue("__btoa", new Func<string, string>(s => Convert.ToBase64String(Encoding.Latin1.GetBytes(s))));
        engine.SetValue("__visualize", new Action<string, string>((template, dataJson) =>
            visualization = Template.Page(request.Name, Template.Render(template, JsonNode.Parse(dataJson)))));
        engine.SetValue("__atob", new Func<string, string>(s => Encoding.Latin1.GetString(DecodeBase64(s))));
        engine.Execute("""
            var __host = {
              envGet: __envGet, envSet: __envSet, envUnset: __envUnset, varGet: __varGet, varSet: __varSet, varUnset: __varUnset,
              globalGet: __globalGet, globalSet: __globalSet, dataGet: __dataGet, resolve: __resolve,
              variablesJson: __variablesJson, iteration: __iteration, requestJson: __requestJson, responseJson: __responseJson,
              test: __test, log: __log, btoa: __btoa, atob: __atob, visualize: __visualize
            };
            """);

        try
        {
            engine.Execute(Prelude);
            engine.Execute(script);
            if (applyRequestChanges)
                ApplyRequestChanges(request, engine.Invoke("__exportRequest").AsString());
            return new ScriptResult(tests, log) { Visualization = visualization };
        }
        catch (JavaScriptException ex)
        {
            return new ScriptResult(tests, log, $"{ex.Error} (line {ex.Location.Start.Line})");
        }
        catch (TimeoutException)
        {
            return new ScriptResult(tests, log, $"Script timed out after {Timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is JintException or Acornima.ParseErrorException or MemoryLimitExceededException
                                       or StatementsCountOverflowException or RecursionDepthOverflowException)
        {
            return new ScriptResult(tests, log, ex.Message);
        }
        catch (ExecutionCanceledException)
        {
            return new ScriptResult(tests, log, "Script cancelled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A CLR exception from a host callback (bad input to atob, a null variable value, ...) must fail the script,
            // not the whole send.
            return new ScriptResult(tests, log, ex.Message);
        }
    }

    /// <summary>Like a browser's <c>atob</c>, but also accepts base64url, whitespace and missing padding.</summary>
    internal static byte[] DecodeBase64(string text)
    {
        var chars = text.Where(c => !char.IsWhiteSpace(c)).Select(c => c switch { '-' => '+', '_' => '/', _ => c }).ToArray();
        var length = Array.IndexOf(chars, '=') is var pad && pad >= 0 ? pad : chars.Length;
        var padded = new string(chars, 0, length).PadRight((length + 3) / 4 * 4, '=');
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            throw new FormatException("atob: the string is not correctly encoded.");
        }
    }

    private static string RequestJson(ApiRequest request) => new JsonObject
    {
        ["name"] = request.Name,
        ["url"] = request.Url,
        ["method"] = request.Method.ToString().ToUpperInvariant(),
        ["bodyMode"] = request.Body.Mode.ToString().ToLowerInvariant(),
        ["body"] = request.Kind == RequestKind.GraphQl ? request.Protocol.GraphQl.Query
            : request.Kind == RequestKind.Grpc ? request.Protocol.Grpc.Message : request.Body.Content,
        ["headers"] = new JsonArray(request.Headers.Where(h => h.IsActive)
            .Select(h => (JsonNode?)new JsonObject { ["key"] = h.Key, ["value"] = h.Value }).ToArray())
    }.ToJsonString();

    private static string ResponseJson(ApiResponse response) => new JsonObject
    {
        ["code"] = response.StatusCode,
        ["status"] = response.ReasonPhrase,
        ["responseTime"] = (long)response.Elapsed.TotalMilliseconds,
        ["responseSize"] = response.SizeBytes,
        ["body"] = response.Body,
        ["headers"] = new JsonArray(response.Headers.Concat(response.Trailers)
            .Select(h => (JsonNode?)new JsonObject { ["key"] = h.Name, ["value"] = h.Value }).ToArray()),
        ["messages"] = new JsonArray(response.Messages.Where(m => m.Direction == MessageDirection.Received)
            .Select(m => (JsonNode?)JsonValue.Create(m.Content)).ToArray())
    }.ToJsonString();

    /// <summary>Copies URL, method, headers and body edits made by a pre-request script back to the request.</summary>
    private static void ApplyRequestChanges(ApiRequest request, string json)
    {
        var node = JsonNode.Parse(json)!;
        request.Url = node["url"]?.GetValue<string>() ?? request.Url;
        if (Enum.TryParse<HttpVerb>(node["method"]?.GetValue<string>(), ignoreCase: true, out var method))
            request.Method = method;

        var headers = (node["headers"] as JsonArray ?? [])
            .Select(h => new KeyValueItem(h?["key"]?.ToString() ?? "", h?["value"]?.ToString() ?? ""))
            .Where(h => h.Key.Length > 0)
            .ToList();
        // Keep disabled rows (the script never saw them) and replace the active ones.
        request.Headers = request.Headers.Where(h => !h.IsActive).Concat(headers).ToList();

        if (node["body"] is JsonValue body && body.TryGetValue<string>(out var raw))
        {
            switch (request.Kind)
            {
                case RequestKind.GraphQl: request.Protocol.GraphQl.Query = raw; break;
                case RequestKind.Grpc: request.Protocol.Grpc.Message = raw; break;
                default: request.Body.Content = raw; break;
            }
        }
    }
}
