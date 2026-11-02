using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Security;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Lint;

/// <summary>One finding of the linter, pointing at a request (or the collection itself when <see cref="RequestId"/> is null).</summary>
public sealed record LintProblem(
    string RuleId,
    string Title,
    LintSeverity Severity,
    Guid? RequestId,
    string RequestName,
    string Folder,
    string Location,
    string Message,
    string Suggestion)
{
    public string SeverityText => Severity.ToString().ToUpperInvariant();

    /// <summary>"Users / Get user" or "(collection)".</summary>
    public string Owner => RequestId is null ? "(collection)" : Folder.Length > 0 ? $"{Folder} / {RequestName}" : RequestName;
}

public sealed class LintReport
{
    public required string CollectionName { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required int RequestCount { get; init; }
    public required IReadOnlyList<LintRule> RulesChecked { get; init; }
    public required IReadOnlyList<LintProblem> Problems { get; init; }

    public int Errors => Problems.Count(p => p.Severity == LintSeverity.Error);
    public int Warnings => Problems.Count(p => p.Severity == LintSeverity.Warning);
    public int Infos => Problems.Count(p => p.Severity == LintSeverity.Info);
    public LintSeverity? Highest => Problems.Count == 0 ? null : Problems.Max(p => p.Severity);

    /// <summary>True when any problem is at or above <paramref name="threshold"/>.</summary>
    public bool FailsAt(LintSeverity threshold) => Problems.Any(p => p.Severity >= threshold);

    public string Summary => Problems.Count == 0
        ? $"No problems in {RequestCount} request(s); {RulesChecked.Count} rule(s) checked."
        : $"{Problems.Count} problem(s) in {RequestCount} request(s): {Errors} error(s), {Warnings} warning(s), {Infos} info.";
}

public sealed class LintOptions
{
    public LintConfig Config { get; init; } = new();

    /// <summary>Environments whose variables count as defined (the active one, or every saved one).</summary>
    public IReadOnlyList<ApiEnvironment> Environments { get; init; } = [];

    /// <summary>Global variables, if any, count as defined too.</summary>
    public IReadOnlyDictionary<string, string>? Globals { get; init; }
}

/// <summary>
/// Static checks over a collection, without sending anything: undefined and unused variables, hard-coded secrets,
/// requests nothing can fail on, duplicates, stale examples and missing descriptions. Pure and deterministic.
/// </summary>
public static partial class CollectionLinter
{
    [GeneratedRegex(@"pm\.(?:environment|variables|collectionVariables|globals)\.set\(\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptSetRegex();

    [GeneratedRegex(@"pm\.(?:environment|variables|collectionVariables|globals)\.(?:get|has|unset)\(\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptGetRegex();

    [GeneratedRegex(@"\bpm\.test\s*\(|\btests\s*\[", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTestRegex();

    [GeneratedRegex(@"^(authorization|proxy-authorization|x-api-key|x-apikey|api-key|apikey|x-auth-token|x-access-token|x-auth|cookie|x-amz-security-token|private-token)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretHeaderRegex();

    [GeneratedRegex(@"(token|secret|passw|api[_-]?key|apikey|access[_-]?key|auth|credential|private[_-]?key|client[_-]?secret)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretNameRegex();

    public static LintReport Lint(RequestCollection collection, LintOptions? options = null)
    {
        options ??= new LintOptions();
        var config = options.Config;
        var rules = LintRules.All.Where(config.IsEnabled).ToList();
        var problems = new List<LintProblem>();
        var requests = collection.Requests.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
        var texts = requests.ToDictionary(r => r.Id, Texts);

        void Add(string ruleId, ApiRequest? request, string location, string message, string suggestion)
        {
            var rule = LintRules.Find(ruleId)!;
            if (!config.IsEnabled(rule))
                return;
            problems.Add(new LintProblem(rule.Id, rule.Title, config.SeverityOf(rule), request?.Id, request?.Name ?? collection.Name,
                request?.Folder ?? "", location, message, suggestion));
        }

        // ---- Variables ---------------------------------------------------------------------------------
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in collection.Variables.Where(v => v.IsActive))
            defined.Add(v.Key.Trim());
        foreach (var env in options.Environments)
            foreach (var key in env.ToDictionary().Keys)
                defined.Add(key);
        foreach (var key in options.Globals?.Keys ?? [])
            defined.Add(key);
        var extracted = new Dictionary<string, List<ApiRequest>>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            foreach (var rule in request.Extractions.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Variable)))
            {
                defined.Add(rule.Variable.Trim());
                if (!extracted.TryGetValue(rule.Variable.Trim(), out var owners))
                    extracted[rule.Variable.Trim()] = owners = [];
                owners.Add(request);
            }
            foreach (Match m in ScriptSetRegex().Matches(request.PreRequestScript + "\n" + request.TestScript))
                defined.Add(m.Groups[1].Value);
        }

        var usedAnywhere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            foreach (var (location, text) in texts[request.Id])
            {
                foreach (var name in VariableResolver.FindNames(text).Distinct(StringComparer.Ordinal))
                {
                    usedAnywhere.Add(name);
                    if (name.StartsWith('$') || defined.Contains(name))
                        continue;
                    Add(LintRules.UndefinedVariable, request, location, $"{{{{{name}}}}} is not defined anywhere.",
                        $"Add '{name}' to the collection variables or an environment, or extract it from an earlier response.");
                }
            }
            foreach (Match m in ScriptGetRegex().Matches(request.PreRequestScript + "\n" + request.TestScript))
                usedAnywhere.Add(m.Groups[1].Value);
        }
        // Collection variables may reference each other ({{baseUrl}} = {{host}}/api).
        foreach (var v in collection.Variables.Where(v => v.IsActive))
            foreach (var name in VariableResolver.FindNames(v.Value))
                usedAnywhere.Add(name);

        foreach (var v in collection.Variables.Where(v => v.IsActive))
            if (!usedAnywhere.Contains(v.Key.Trim()))
                Add(LintRules.UnusedVariable, null, $"variable {v.Key}", $"Collection variable '{v.Key}' is never used.",
                    "Remove it, or reference it as {{" + v.Key.Trim() + "}} where it is needed.");

        foreach (var (name, owners) in extracted)
            if (!usedAnywhere.Contains(name))
                foreach (var owner in owners)
                    Add(LintRules.UnusedExtraction, owner, $"extraction {name}", $"'{name}' is extracted but nothing in the collection uses it.",
                        "Remove the extraction rule, or use {{" + name + "}} in a later request.");

        // ---- Secrets -----------------------------------------------------------------------------------
        foreach (var v in collection.Variables.Where(v => v.IsActive && !v.IsSecret && v.Value.Length > 0))
        {
            var shape = PassiveChecks.SecretMatches(v.Value).FirstOrDefault();
            if (shape.Kind is not null || SecretNameRegex().IsMatch(v.Key) && !LooksLikePlaceholder(v.Value))
                Add(LintRules.HardcodedSecret, null, $"variable {v.Key}",
                    $"Collection variable '{v.Key}' holds {(shape.Kind is null ? "what looks like a credential" : "a " + shape.Kind)} ({Mask(v.Value)}). Collection variables are exported and committed in plain text.",
                    "Move it to an environment and mark it secret; keep only a placeholder in the collection.");
        }

        foreach (var request in requests)
        {
            foreach (var (location, text) in texts[request.Id])
            {
                if (text.Length == 0 || LooksLikePlaceholder(text))
                    continue;
                var (kind, evidence) = PassiveChecks.SecretMatches(text).FirstOrDefault();
                if (kind is not null)
                    Add(LintRules.HardcodedSecret, request, location, $"Contains a {kind} ({Mask(evidence)}).",
                        "Replace it with a {{variable}} defined in an environment as a secret.");
            }
            foreach (var h in request.Headers.Where(h => h.IsActive && h.Value.Trim().Length > 0 && SecretHeaderRegex().IsMatch(h.Key.Trim())))
                if (!LooksLikePlaceholder(h.Value) && !PassiveChecks.SecretMatches(h.Value).Any())
                    Add(LintRules.HardcodedSecret, request, $"header {h.Key}", $"Header {h.Key} carries a literal credential ({Mask(h.Value)}).",
                        "Use the Auth tab with a {{variable}}, or put the value in a secret environment variable.");
            var queryPairs = request.QueryParams.Where(p => p.IsActive).Select(p => (Key: p.Key.Trim(), p.Value))
                .Concat(UrlQueryPairs(request.Url));
            foreach (var (key, value) in queryPairs.Where(p => p.Value.Trim().Length > 0 && SecretNameRegex().IsMatch(p.Key)))
                if (!LooksLikePlaceholder(value) && !PassiveChecks.SecretMatches(value).Any())
                    Add(LintRules.HardcodedSecret, request, $"query {key}", $"Query parameter {key} carries a literal credential ({Mask(value)}).",
                        "Reference a secret environment variable instead: {{" + key + "}}.");
            foreach (var (field, value) in AuthSecrets(request.Auth))
                if (value.Length > 0 && !LooksLikePlaceholder(value) && !PassiveChecks.SecretMatches(value).Any())
                    Add(LintRules.HardcodedSecret, request, $"auth {field}", $"Auth {field} is a literal value ({Mask(value)}).",
                        "Reference a secret environment variable instead, e.g. {{" + field.ToLowerInvariant() + "}}.");
        }

        // ---- Assertions --------------------------------------------------------------------------------
        foreach (var request in requests)
        {
            var hasCheck = request.Assertions.Any(a => a.Enabled)
                           || request.Expectations.Any(e => e.Enabled)
                           || ScriptTestRegex().IsMatch(request.TestScript);
            if (!hasCheck)
                Add(LintRules.NoAssertions, request, "checks", "No assertion, test script check or message expectation: a run can never fail on this request.",
                    "Add at least a status assertion (Status equals 200) in the Checks tab.");
        }

        // ---- Duplicates --------------------------------------------------------------------------------
        foreach (var group in requests.GroupBy(Fingerprint).Where(g => g.Count() > 1))
        {
            var first = group.First();
            foreach (var duplicate in group.Skip(1))
                Add(LintRules.DuplicateRequest, duplicate, "request",
                    $"Sends the same {Verb(duplicate)} {duplicate.Url} with the same body as '{first.Name}'{(first.Folder.Length > 0 ? $" ({first.Folder})" : "")}.",
                    "Delete one of them, or make them differ (parameters, body, description) if both are intended.");
        }
        foreach (var group in requests.GroupBy(r => (r.Folder.Trim(), r.Name.Trim()), new FolderNameComparer()).Where(g => g.Count() > 1))
            foreach (var duplicate in group.Skip(1))
                Add(LintRules.DuplicateName, duplicate, "name", $"Another request in the same folder is also named '{duplicate.Name}'.",
                    "Rename one of them so --request filters, flows and reports can tell them apart.");

        // ---- Examples ----------------------------------------------------------------------------------
        foreach (var request in requests)
        {
            var queryKeys = request.QueryParams.Where(p => p.IsActive).Select(p => p.Key.Trim())
                .Concat(UrlQueryKeys(request.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var example in request.Examples)
            {
                var location = $"example {example.Name}";
                foreach (var rule in example.MatchQuery.Where(q => q.IsActive && !queryKeys.Contains(q.Key.Trim())))
                    Add(LintRules.StaleExample, request, location,
                        $"Matches query parameter '{rule.Key}', which the request no longer sends, so the mock server would never serve it.",
                        $"Add '{rule.Key}' back to the request, or remove the match rule from the example.");
                if (example.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) && example.Body.Trim().Length > 0 && !IsJson(example.Body))
                    Add(LintRules.StaleExample, request, location, $"Content type is {example.ContentType} but the body is not valid JSON.",
                        "Fix the body or change the content type.");
                if (example.Schema.Trim().Length > 0 && example.Body.Trim().Length > 0 && SchemaTopType(example.Schema) is { } expected
                    && JsonTopType(example.Body) is { } actual && expected != actual)
                    Add(LintRules.StaleExample, request, location, $"The schema describes {expected} but the body is {actual}.",
                        "Re-save the example from a real response, or update its schema.");
                if (example.Session.Count > 0 && request.Kind is RequestKind.Http or RequestKind.GraphQl or RequestKind.Grpc or RequestKind.Soap)
                    Add(LintRules.StaleExample, request, location, $"Holds a recorded streaming session, but the request is {request.Kind}.",
                        "Delete the example, or move it to the streaming request it was recorded from.");
            }
        }

        // ---- Descriptions and URLs ---------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(collection.Description))
            Add(LintRules.MissingDescription, null, "description", "The collection has no description.",
                "Describe the API in Collection settings; it becomes the intro of the generated docs.");
        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.Description))
                Add(LintRules.MissingDescription, request, "description", "The request has no description.", "Add one in the request's Docs tab.");
            if (string.IsNullOrWhiteSpace(request.Url))
                Add(LintRules.EmptyUrl, request, "url", "The request has no URL.", "Enter the endpoint, e.g. {{baseUrl}}/users.");
        }

        return new LintReport
        {
            CollectionName = collection.Name,
            RequestCount = requests.Count,
            RulesChecked = rules,
            Problems = problems.OrderByDescending(p => p.Severity).ThenBy(p => p.RuleId, StringComparer.Ordinal)
                .ThenBy(p => p.RequestId is null ? -1 : requests.FindIndex(r => r.Id == p.RequestId)).ToList()
        };
    }

    /// <summary>Every text of a request that may hold variables or secrets, with a human location.</summary>
    private static List<(string Location, string Text)> Texts(ApiRequest r)
    {
        var list = new List<(string, string)> { ("url", r.Url) };
        foreach (var p in r.QueryParams.Where(p => p.IsActive))
            list.Add(($"query {p.Key}", p.Value));
        foreach (var h in r.Headers.Where(h => h.IsActive))
            list.Add(($"header {h.Key}", h.Value));
        if (r.Body.Mode != BodyMode.None)
            list.Add(("body", r.Body.Content));
        foreach (var f in r.Body.FormFields.Where(f => f.IsActive && !f.IsFile))
            list.Add(($"form {f.Key}", f.Value));
        foreach (var (field, value) in AuthTexts(r.Auth))
            list.Add(($"auth {field}", value));
        for (var i = 0; i < r.Assertions.Count; i++)
            if (r.Assertions[i].Enabled)
                list.Add(($"assertion {i + 1}", r.Assertions[i].Path + "\n" + r.Assertions[i].Expected));
        foreach (var e in r.Extractions.Where(e => e.Enabled))
            list.Add(($"extraction {e.Variable}", e.Path));
        foreach (var e in r.Expectations.Where(e => e.Enabled))
            list.Add(("message check", e.Path + "\n" + e.Expected + "\n" + e.Channel));
        if (r.PreRequestScript.Length > 0)
            list.Add(("pre-request script", r.PreRequestScript));
        if (r.TestScript.Length > 0)
            list.Add(("test script", r.TestScript));
        // Protocol settings (GraphQL query and variables, gRPC message, broker topics, ...) as one text.
        var protocol = JsonSerializer.Serialize(r.Protocol);
        if (protocol.Contains("{{", StringComparison.Ordinal) || PassiveChecks.SecretMatches(protocol).Any())
            list.Add(("protocol settings", protocol));
        return list;
    }

    private static IEnumerable<(string Field, string Value)> AuthTexts(AuthSettings a) =>
        AuthSecrets(a).Concat(
        [
            ("Username", a.Username), ("ApiKeyName", a.ApiKeyName), ("OAuth2TokenUrl", a.OAuth2TokenUrl), ("OAuth2AuthUrl", a.OAuth2AuthUrl),
            ("OAuth2ClientId", a.OAuth2ClientId), ("OAuth2Scope", a.OAuth2Scope), ("OAuth2Audience", a.OAuth2Audience), ("AwsAccessKey", a.AwsAccessKey)
        ]);

    /// <summary>The auth fields that must never hold a literal credential.</summary>
    private static IEnumerable<(string Field, string Value)> AuthSecrets(AuthSettings a) => a.Mode switch
    {
        AuthMode.None => [],
        AuthMode.Bearer => [("Token", a.Token)],
        AuthMode.Basic or AuthMode.Digest or AuthMode.Ntlm => [("Password", a.Password)],
        AuthMode.ApiKey => [("ApiKeyValue", a.ApiKeyValue)],
        AuthMode.OAuth2 => [("OAuth2ClientSecret", a.OAuth2ClientSecret), ("Password", a.Password)],
        AuthMode.AwsSigV4 => [("AwsSecretKey", a.AwsSecretKey), ("AwsSessionToken", a.AwsSessionToken)],
        _ => []
    };

    private static bool LooksLikePlaceholder(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains("{{", StringComparison.Ordinal))
            return true;
        // Obvious placeholders people leave in templates.
        return trimmed.Length < 4 || trimmed.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("Basic", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("xxx", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("your-", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("<", StringComparison.Ordinal) || trimmed.Contains("changeme", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("example", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shows just enough of a secret to recognise it: "AKIA…XYZ1" (never the whole value).</summary>
    public static string Mask(string value)
    {
        var v = value.Trim().ReplaceLineEndings(" ");
        if (v.Length <= 8)
            return new string('•', v.Length);
        return v[..4] + "…" + v[^3..];
    }

    private static string Verb(ApiRequest r) => r.Kind == RequestKind.Http ? r.Method.ToString().ToUpperInvariant() : r.Kind.ToString();

    private static string Fingerprint(ApiRequest r)
    {
        var url = r.Url.Trim().TrimEnd('/');
        var query = string.Join("&", r.QueryParams.Where(p => p.IsActive).Select(p => p.Key.Trim() + "=" + p.Value.Trim()).OrderBy(s => s, StringComparer.Ordinal));
        var body = r.Body.Mode == BodyMode.None ? "" : r.Body.Mode + ":" + Regex.Replace(r.Body.Content, @"\s+", "") + ":" +
            string.Join("&", r.Body.FormFields.Where(f => f.IsActive).Select(f => f.Key + "=" + f.Value).OrderBy(s => s, StringComparer.Ordinal));
        var protocol = r.Kind == RequestKind.Http ? "" : JsonSerializer.Serialize(r.Protocol);
        return string.Join("\n", r.Kind, r.Kind == RequestKind.Http ? r.Method : HttpVerb.Get, url, query, body, protocol);
    }

    private static IEnumerable<string> UrlQueryKeys(string url) => UrlQueryPairs(url).Select(p => p.Key);

    private static IEnumerable<(string Key, string Value)> UrlQueryPairs(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0)
            return [];
        return url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(parts => (Key: parts[0].Trim(), Value: parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : ""))
            .Where(p => p.Key.Length > 0);
    }

    private static bool IsJson(string text)
    {
        try
        {
            JsonNode.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? JsonTopType(string body)
    {
        try
        {
            return JsonNode.Parse(body) switch
            {
                JsonObject => "an object",
                JsonArray => "an array",
                null => null,
                _ => "a scalar"
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SchemaTopType(string schema)
    {
        try
        {
            var type = (JsonNode.Parse(schema) as JsonObject)?["type"];
            var text = type is JsonArray array ? array.FirstOrDefault(t => t?.ToString() != "null")?.ToString() : type?.ToString();
            return text switch
            {
                "object" => "an object",
                "array" => "an array",
                "string" or "number" or "integer" or "boolean" => "a scalar",
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class FolderNameComparer : IEqualityComparer<(string Folder, string Name)>
    {
        public bool Equals((string Folder, string Name) x, (string Folder, string Name) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string Name) obj) =>
            HashCode.Combine(obj.Folder.ToLowerInvariant(), obj.Name.ToLowerInvariant());
    }
}
