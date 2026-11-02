using Dispatch.Application.Impact;
using Dispatch.Application.Requests;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.Application.Spec;

public enum ChangeSeverity
{
    /// <summary>Existing clients or tests will fail: something they rely on is gone, retyped or newly required.</summary>
    Breaking,

    /// <summary>Additive or relaxing: existing clients keep working.</summary>
    NonBreaking,

    /// <summary>Worth knowing, no compatibility effect (deprecations, servers, version).</summary>
    Info
}

/// <summary>One difference between two versions of a spec.</summary>
public sealed record SpecChange(ChangeSeverity Severity, string Category, string Operation, string Location, string Description, ShapeChange? Shape = null)
{
    public string SeverityText => Severity switch
    {
        ChangeSeverity.Breaking => "BREAKING",
        ChangeSeverity.NonBreaking => "ok",
        _ => "info"
    };

    public override string ToString() => $"{Operation} · {Location}: {Description}";
}

/// <summary>Something in the collection that a spec change breaks (or may break).</summary>
public sealed record SpecImpactItem(ImpactSeverity Severity, string Kind, string Owner, string Location, string Detail, string? Suggestion, string Change)
{
    public string SeverityText => Severity == ImpactSeverity.Breaks ? "BREAKS" : "POSSIBLE";
}

public sealed class SpecDiffReport
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required string OldName { get; init; }
    public required string NewName { get; init; }
    public List<SpecChange> Changes { get; } = [];
    public List<SpecImpactItem> Impact { get; } = [];

    /// <summary>Name of the collection the impact was computed for, when any.</summary>
    public string? CollectionName { get; set; }

    public bool HasBreaking => Changes.Any(c => c.Severity == ChangeSeverity.Breaking);
    public int Count(ChangeSeverity severity) => Changes.Count(c => c.Severity == severity);
    public bool HasBreakingImpact => Impact.Any(i => i.Severity == ImpactSeverity.Breaks);

    public string Summary
    {
        get
        {
            if (Changes.Count == 0)
                return "No differences between the two spec versions.";
            var breaking = Count(ChangeSeverity.Breaking);
            var text = breaking == 0
                ? $"{Changes.Count} change{(Changes.Count == 1 ? "" : "s")}, none breaking."
                : $"{breaking} breaking change{(breaking == 1 ? "" : "s")} and {Changes.Count - breaking} other{(Changes.Count - breaking == 1 ? "" : "s")}.";
            if (CollectionName is null)
                return text;
            var breaks = Impact.Count(i => i.Severity == ImpactSeverity.Breaks);
            var possible = Impact.Count - breaks;
            return text + (Impact.Count == 0
                ? $" Nothing in {CollectionName} is affected."
                : $" In {CollectionName}: {breaks} item{(breaks == 1 ? "" : "s")} break{(breaks == 1 ? "s" : "")}" + (possible > 0 ? $", {possible} more to check." : "."));
        }
    }
}

/// <summary>
/// Breaking-change detector: compares two versions of an OpenAPI / Swagger document and classifies every difference
/// (operations, parameters, request and response schemas, status codes, security) as breaking, non-breaking or info.
/// Response shape changes are expressed as <see cref="ShapeChange"/>s so the change impact analyzer can list the tests,
/// extractions, requests and flows each one breaks.
/// </summary>
public static class SpecDiff
{
    public static SpecDiffReport Compare(SpecDocument before, SpecDocument after, string? oldName = null, string? newName = null)
    {
        var report = new SpecDiffReport { OldName = oldName ?? Describe(before), NewName = newName ?? Describe(after) };
        var changes = report.Changes;

        if (before.Version != after.Version)
            changes.Add(new SpecChange(ChangeSeverity.Info, "info", "*", "version", $"version {Q(before.Version)} → {Q(after.Version)}"));
        if (!before.Servers.SequenceEqual(after.Servers, StringComparer.Ordinal) && (before.Servers.Count > 0 || after.Servers.Count > 0))
            changes.Add(new SpecChange(ChangeSeverity.Info, "servers", "*", "servers",
                $"servers {string.Join(", ", before.Servers.DefaultIfEmpty("(none)"))} → {string.Join(", ", after.Servers.DefaultIfEmpty("(none)"))}"));

        var oldOps = before.Operations.ToDictionary(o => o.Key, StringComparer.Ordinal);
        var newOps = after.Operations.ToDictionary(o => o.Key, StringComparer.Ordinal);
        var removed = before.Operations.Where(o => !newOps.ContainsKey(o.Key)).ToList();
        var added = after.Operations.Where(o => !oldOps.ContainsKey(o.Key)).ToList();

        // Moved paths: same operationId, or same method with one segment different and the same status codes.
        var pairs = new List<(SpecOperation Old, SpecOperation New)>();
        foreach (var op in removed)
        {
            var candidate = added.Where(a => !pairs.Any(p => p.New == a)).FirstOrDefault(a => a.Method == op.Method && op.OperationId is { Length: > 0 } && a.OperationId == op.OperationId)
                            ?? added.Where(a => !pairs.Any(p => p.New == a)).FirstOrDefault(a => LooksMoved(op, a));
            if (candidate is not null)
                pairs.Add((op, candidate));
        }
        foreach (var (oldOp, newOp) in pairs)
        {
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "operation", oldOp.Key, "path", $"moved to {newOp.Path}"));
            CompareOperations(before, after, oldOp, newOp, changes, oldOp.Key);
        }
        var removedPaths = removed.Except(pairs.Select(p => p.Old)).Select(o => o.Path).Distinct()
            .Where(path => !after.Operations.Any(o => o.Path == path)).ToHashSet(StringComparer.Ordinal);
        foreach (var op in removed.Except(pairs.Select(p => p.Old)))
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "operation", op.Key, "operation",
                removedPaths.Contains(op.Path) ? "removed (the whole path is gone)" : "removed"));
        foreach (var op in added.Except(pairs.Select(p => p.New)))
            changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "operation", op.Key, "operation", "added"));

        foreach (var oldOp in before.Operations.Where(o => newOps.ContainsKey(o.Key)))
            CompareOperations(before, after, oldOp, newOps[oldOp.Key], changes, oldOp.Key);

        // Stable order: breaking first, then by operation.
        var ordered = changes.OrderBy(c => c.Severity).ThenBy(c => c.Operation == "*" ? 1 : 0).ThenBy(c => c.Operation, StringComparer.Ordinal).ToList();
        changes.Clear();
        changes.AddRange(ordered);
        return report;
    }

    private static bool LooksMoved(SpecOperation a, SpecOperation b)
    {
        if (a.Method != b.Method)
            return false;
        var sa = a.Path.Split('/');
        var sb = b.Path.Split('/');
        if (sa.Length != sb.Length)
            return false;
        var different = sa.Zip(sb).Count(p => !string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
        var codes = a.Responses.Select(r => r.Code).OrderBy(c => c).SequenceEqual(b.Responses.Select(r => r.Code).OrderBy(c => c));
        return different == 1 && codes;
    }

    private static void CompareOperations(SpecDocument before, SpecDocument after, SpecOperation oldOp, SpecOperation newOp, List<SpecChange> changes, string key)
    {
        if (!oldOp.Deprecated && newOp.Deprecated)
            changes.Add(new SpecChange(ChangeSeverity.Info, "operation", key, "operation", "deprecated"));

        // ---- Parameters ----
        var oldParams = oldOp.Parameters.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var newParams = newOp.Parameters.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var p in oldOp.Parameters.Where(p => !newParams.ContainsKey(p.Key)))
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "parameter", key, $"{p.In} {p.Name}", "removed parameter"));
        foreach (var p in newOp.Parameters.Where(p => !oldParams.ContainsKey(p.Key)))
            changes.Add(p.Required && p.In != "path"
                ? new SpecChange(ChangeSeverity.Breaking, "parameter", key, $"{p.In} {p.Name}", "new required parameter")
                : new SpecChange(ChangeSeverity.NonBreaking, "parameter", key, $"{p.In} {p.Name}", p.In == "path" ? "new path parameter" : "new optional parameter"));
        foreach (var p in oldOp.Parameters.Where(p => newParams.ContainsKey(p.Key)))
        {
            var n = newParams[p.Key];
            var where = $"{p.In} {p.Name}";
            if (!p.Required && n.Required)
                changes.Add(new SpecChange(ChangeSeverity.Breaking, "parameter", key, where, "became required"));
            else if (p.Required && !n.Required && p.In != "path")
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "parameter", key, where, "became optional"));
            if (p.Type != n.Type && p.Type != "any" && n.Type != "any")
                changes.Add(new SpecChange(ChangeSeverity.Breaking, "parameter", key, where, $"type {p.Type} → {n.Type}"));
            // An absent enum means unrestricted: gaining one narrows the parameter, losing one widens it.
            var removedValues = p.Enum.Except(n.Enum, StringComparer.Ordinal).ToList();
            var addedValues = n.Enum.Except(p.Enum, StringComparer.Ordinal).ToList();
            if (p.Enum.Count == 0 && n.Enum.Count > 0)
                changes.Add(new SpecChange(ChangeSeverity.Breaking, "parameter", key, where, $"now restricted to {string.Join(", ", n.Enum.Take(8))}"));
            else if (p.Enum.Count > 0 && n.Enum.Count == 0)
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "parameter", key, where, "no longer restricted to an enum"));
            else
            {
                if (removedValues.Count > 0)
                    changes.Add(new SpecChange(ChangeSeverity.Breaking, "parameter", key, where, $"enum narrowed: removed {string.Join(", ", removedValues)}"));
                if (addedValues.Count > 0)
                    changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "parameter", key, where, $"enum widened: added {string.Join(", ", addedValues)}"));
            }
        }

        // ---- Request body ----
        if (!oldOp.RequestBodyRequired && newOp.RequestBodyRequired)
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "request", key, "request body", "became required"));
        else if (oldOp.RequestBodyRequired && !newOp.RequestBodyRequired)
            changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "request", key, "request body", "became optional"));
        foreach (var media in oldOp.RequestMediaTypes.Except(newOp.RequestMediaTypes, StringComparer.OrdinalIgnoreCase))
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "request", key, "request body", $"no longer accepts {media}"));
        foreach (var media in newOp.RequestMediaTypes.Except(oldOp.RequestMediaTypes, StringComparer.OrdinalIgnoreCase))
            changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "request", key, "request body", $"now accepts {media}"));
        if (oldOp.RequestSchema is not null && newOp.RequestSchema is null && newOp.RequestMediaTypes.Count == 0 && oldOp.RequestMediaTypes.Count > 0)
            changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "request", key, "request body", "removed"));
        else
            CompareSchemas(before, after, oldOp.RequestSchema, newOp.RequestSchema, isResponse: false, key, "request body", changes);

        // ---- Responses ----
        var oldResponses = oldOp.Responses.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        var newResponses = newOp.Responses.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        var removedCodes = oldOp.Responses.Where(r => !newResponses.ContainsKey(r.Code)).ToList();
        var addedCodes = newOp.Responses.Where(r => !oldResponses.ContainsKey(r.Code)).ToList();
        // A single success status replaced by another: "200 → 201".
        var removedSuccess = removedCodes.Where(r => r.Code.StartsWith('2')).ToList();
        var addedSuccess = addedCodes.Where(r => r.Code.StartsWith('2')).ToList();
        if (removedSuccess.Count == 1 && addedSuccess.Count == 1 && !newOp.Responses.Any(r => r.Code.StartsWith('2') && oldResponses.ContainsKey(r.Code)))
        {
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "response", key, "status", $"success status {removedSuccess[0].Code} → {addedSuccess[0].Code}"));
            CompareSchemas(before, after, removedSuccess[0].Schema, addedSuccess[0].Schema, isResponse: true, key, $"response {addedSuccess[0].Code}", changes);
            removedCodes.Remove(removedSuccess[0]);
            addedCodes.Remove(addedSuccess[0]);
        }
        foreach (var r in removedCodes)
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "response", key, $"response {r.Code}", "removed status"));
        foreach (var r in addedCodes)
            changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "response", key, $"response {r.Code}", "new status"));
        foreach (var r in oldOp.Responses.Where(r => newResponses.ContainsKey(r.Code)))
        {
            var n = newResponses[r.Code];
            var where = $"response {r.Code}";
            foreach (var media in r.MediaTypes.Except(n.MediaTypes, StringComparer.OrdinalIgnoreCase))
                changes.Add(new SpecChange(ChangeSeverity.Breaking, "response", key, where, $"no longer returns {media}"));
            foreach (var media in n.MediaTypes.Except(r.MediaTypes, StringComparer.OrdinalIgnoreCase))
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "response", key, where, $"may now return {media}"));
            foreach (var header in r.RequiredHeaders.Except(n.RequiredHeaders, StringComparer.OrdinalIgnoreCase))
                changes.Add(new SpecChange(ChangeSeverity.Breaking, "response", key, where, $"header {header} no longer guaranteed"));
            foreach (var header in n.RequiredHeaders.Except(r.RequiredHeaders, StringComparer.OrdinalIgnoreCase))
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "response", key, where, $"header {header} now guaranteed"));
            CompareSchemas(before, after, r.Schema, n.Schema, isResponse: true, key, where, changes);
        }

        // ---- Security ----
        // Requirements are alternatives (any one satisfies) of schemes needed together. Old clients keep working when every
        // old alternative still satisfies some new alternative (the new one needs a subset of what the client already sends).
        var oldReqs = oldOp.SecurityRequirements;
        var newReqs = newOp.SecurityRequirements;
        if (oldOp.AllowsAnonymous && !newOp.AllowsAnonymous)
            changes.Add(new SpecChange(ChangeSeverity.Breaking, "security", key, "security", $"now requires authentication ({Requirements(newReqs)})"));
        else if (!oldOp.AllowsAnonymous && newOp.AllowsAnonymous)
            changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "security", key, "security", "no longer requires authentication"));
        if (!oldOp.AllowsAnonymous && !newOp.AllowsAnonymous)
        {
            foreach (var old in oldReqs.Where(o => !newReqs.Any(n => n.All(scheme => o.Contains(scheme, StringComparer.Ordinal)))))
                changes.Add(new SpecChange(ChangeSeverity.Breaking, "security", key, "security",
                    $"no longer accepts {Requirement(old)}" + (newReqs.Count > 0 ? $"; now needs {Requirements(newReqs)}" : "")));
            foreach (var added in newReqs.Where(n => !oldReqs.Any(o => n.All(scheme => o.Contains(scheme, StringComparer.Ordinal)))))
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, "security", key, "security", $"now also accepts {Requirement(added)}"));
        }
        foreach (var scheme in oldOp.Security.Intersect(newOp.Security, StringComparer.Ordinal))
            if (before.SecuritySchemes.TryGetValue(scheme, out var a) && after.SecuritySchemes.TryGetValue(scheme, out var b) && a != b)
                changes.Add(new SpecChange(ChangeSeverity.Breaking, "security", key, "security", $"scheme {scheme} changed: {a} → {b}"));
    }

    private static string Requirement(IReadOnlyList<string> schemes) => schemes.Count == 0 ? "anonymous" : string.Join(" + ", schemes);

    private static string Requirements(IReadOnlyList<IReadOnlyList<string>> requirements) =>
        string.Join(" or ", requirements.Where(r => r.Count > 0).Select(Requirement));

    private static string SchemeText(SpecDocument spec, string scheme) => spec.SecuritySchemes.TryGetValue(scheme, out var t) && t.Length > 0 ? $" ({t})" : "";

    /// <summary>
    /// Field-level comparison. Request and response schemas have opposite rules: a field a client must now send breaks
    /// requests; a field a client can no longer read breaks responses.
    /// </summary>
    private static void CompareSchemas(SpecDocument before, SpecDocument after, System.Text.Json.Nodes.JsonNode? oldSchema, System.Text.Json.Nodes.JsonNode? newSchema,
        bool isResponse, string key, string where, List<SpecChange> changes)
    {
        if (oldSchema is null && newSchema is null)
            return;
        var category = isResponse ? "response" : "request";
        var oldFields = SchemaShape.Flatten(before.Root, oldSchema);
        var newFields = SchemaShape.Flatten(after.Root, newSchema);
        if (oldSchema is null || newSchema is null)
        {
            if (oldFields.Count > 0 && newFields.Count == 0 && isResponse)
                changes.Add(new SpecChange(ChangeSeverity.Breaking, category, key, where, "body no longer documented"));
            else if (oldFields.Count == 0 && newFields.Count > 0)
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, category, key, where, "body now documented"));
            return;
        }

        var removedPaths = oldFields.Keys.Where(p => !newFields.ContainsKey(p)).ToList();
        var addedPaths = newFields.Keys.Where(p => !oldFields.ContainsKey(p)).ToList();
        // Only the top of a removed / added subtree.
        removedPaths = removedPaths.Where(p => !removedPaths.Any(o => o != p && p.StartsWith(o + ".", StringComparison.Ordinal) || o != p && p.StartsWith(o + "[", StringComparison.Ordinal))).ToList();
        addedPaths = addedPaths.Where(p => !addedPaths.Any(o => o != p && p.StartsWith(o + ".", StringComparison.Ordinal) || o != p && p.StartsWith(o + "[", StringComparison.Ordinal))).ToList();

        // Renames: the only same-typed removed/added pair under one parent.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var from in removedPaths)
        {
            var parent = Parent(from);
            var type = oldFields[from].TypeText;
            var siblingsRemoved = removedPaths.Where(p => Parent(p) == parent && oldFields[p].TypeText == type).ToList();
            var candidates = addedPaths.Where(p => Parent(p) == parent && newFields[p].TypeText == type && !renamed.ContainsValue(p)).ToList();
            if (siblingsRemoved.Count == 1 && candidates.Count == 1)
                renamed[from] = candidates[0];
        }

        foreach (var path in removedPaths)
        {
            var field = oldFields[path];
            if (renamed.TryGetValue(path, out var to))
            {
                changes.Add(new SpecChange(ChangeSeverity.Breaking, category, key, where, $"renamed {path} → {to}",
                    Shape(ShapeChangeKind.Renamed, path, field.TypeText, newFields[to].TypeText, to)));
                continue;
            }
            if (isResponse)
                changes.Add(new SpecChange(ChangeSeverity.Breaking, category, key, where, $"removed {path} ({field.TypeText})", Shape(ShapeChangeKind.Removed, path, field.TypeText)));
            else
                changes.Add(new SpecChange(field.ReadOnly ? ChangeSeverity.NonBreaking : ChangeSeverity.Breaking, category, key, where,
                    $"removed {path} ({field.TypeText})" + (field.ReadOnly ? "" : "; clients still sending it may be rejected"), Shape(ShapeChangeKind.Removed, path, field.TypeText)));
        }
        foreach (var path in addedPaths.Where(p => !renamed.ContainsValue(p)))
        {
            var field = newFields[path];
            if (!isResponse && field.Required && !field.ReadOnly && path.Count(c => c == '.') + path.Count(c => c == '[') <= 1)
                changes.Add(new SpecChange(ChangeSeverity.Breaking, category, key, where, $"new required field {path} ({field.TypeText})", Shape(ShapeChangeKind.Added, path, newType: field.TypeText)));
            else if (!isResponse && field.Required && !field.ReadOnly)
                changes.Add(new SpecChange(ChangeSeverity.Breaking, category, key, where, $"new required field {path} ({field.TypeText}) inside an existing object", Shape(ShapeChangeKind.Added, path, newType: field.TypeText)));
            else
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, category, key, where, $"added {path} ({field.TypeText})", Shape(ShapeChangeKind.Added, path, newType: field.TypeText)));
        }

        foreach (var path in oldFields.Keys.Where(newFields.ContainsKey))
        {
            var o = oldFields[path];
            var n = newFields[path];
            if (o.Types.Count > 0 && n.Types.Count > 0 && !o.Types.SetEquals(n.Types))
            {
                var widened = o.Types.IsProperSubsetOf(n.Types);
                var severity = isResponse ? ChangeSeverity.Breaking : widened ? ChangeSeverity.NonBreaking : ChangeSeverity.Breaking;
                changes.Add(new SpecChange(severity, category, key, where, $"{path} type {o.TypeText} → {n.TypeText}", Shape(ShapeChangeKind.TypeChanged, path, o.TypeText, n.TypeText)));
            }
            if (o.Required != n.Required && path != "$")
            {
                if (isResponse)
                    changes.Add(n.Required
                        ? new SpecChange(ChangeSeverity.NonBreaking, category, key, where, $"{path} is now always present")
                        : new SpecChange(ChangeSeverity.Breaking, category, key, where, $"{path} is no longer guaranteed (optional now)"));
                else
                    changes.Add(n.Required && !n.ReadOnly
                        ? new SpecChange(ChangeSeverity.Breaking, category, key, where, $"{path} became required")
                        : new SpecChange(ChangeSeverity.NonBreaking, category, key, where, $"{path} became optional"));
            }
            if (o.Nullable != n.Nullable)
            {
                if (isResponse)
                    changes.Add(n.Nullable
                        ? new SpecChange(ChangeSeverity.Breaking, category, key, where, $"{path} may now be null")
                        : new SpecChange(ChangeSeverity.NonBreaking, category, key, where, $"{path} is no longer nullable"));
                else
                    changes.Add(n.Nullable
                        ? new SpecChange(ChangeSeverity.NonBreaking, category, key, where, $"{path} accepts null now")
                        : new SpecChange(ChangeSeverity.Breaking, category, key, where, $"{path} no longer accepts null"));
            }
            var removedValues = o.Enum.Except(n.Enum, StringComparer.Ordinal).ToList();
            var addedValues = n.Enum.Except(o.Enum, StringComparer.Ordinal).ToList();
            if (o.Enum.Count > 0 && n.Enum.Count > 0)
            {
                if (removedValues.Count > 0)
                    changes.Add(new SpecChange(isResponse ? ChangeSeverity.NonBreaking : ChangeSeverity.Breaking, category, key, where,
                        $"{path} enum narrowed: removed {string.Join(", ", removedValues)}"));
                if (addedValues.Count > 0)
                    changes.Add(new SpecChange(isResponse ? ChangeSeverity.Breaking : ChangeSeverity.NonBreaking, category, key, where,
                        $"{path} enum widened: added {string.Join(", ", addedValues)}" + (isResponse ? " (clients may not handle them)" : "")));
            }
            else if (o.Enum.Count == 0 && n.Enum.Count > 0)
                changes.Add(new SpecChange(isResponse ? ChangeSeverity.NonBreaking : ChangeSeverity.Breaking, category, key, where,
                    $"{path} is now restricted to {string.Join(", ", n.Enum.Take(8))}"));
            else if (o.Enum.Count > 0 && n.Enum.Count == 0)
                changes.Add(new SpecChange(isResponse ? ChangeSeverity.Breaking : ChangeSeverity.NonBreaking, category, key, where,
                    $"{path} is no longer restricted to an enum" + (isResponse ? " (clients may not handle new values)" : "")));
            if (o.Format != n.Format && o.Format is not null && n.Format is not null)
                changes.Add(new SpecChange(ChangeSeverity.NonBreaking, category, key, where, $"{path} format {o.Format} → {n.Format}"));
        }
    }

    private static ShapeChange Shape(ShapeChangeKind kind, string path, string? oldType = null, string? newType = null, string? newPath = null) =>
        new(kind, path, oldType, newType, newPath) { Segments = JsonShape.Split(path) };

    private static string Parent(string path)
    {
        var dot = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
        return dot <= 0 ? "$" : path[..dot];
    }

    private static string Describe(SpecDocument spec) => spec.Title + (spec.Version.Length > 0 ? $" {spec.Version}" : "");

    private static string Q(string s) => s.Length == 0 ? "(none)" : s;

    // ---- Impact on a collection ----------------------------------------------------------------

    /// <summary>
    /// Finds what in <paramref name="collection"/> the changes break: requests that call removed operations or send removed
    /// parameters, requests missing a new required parameter or body field, status assertions on a changed success code, and
    /// (through <see cref="ImpactAnalyzer"/>) the tests, extractions, dependent requests and flows that read changed response fields.
    /// </summary>
    public static void AddImpact(SpecDiffReport report, SpecDocument before, RequestCollection collection, IReadOnlyList<TestFlow>? flows = null)
    {
        report.CollectionName = collection.Name;
        var byOperation = new Dictionary<string, List<ApiRequest>>(StringComparer.Ordinal);
        foreach (var request in collection.Requests.Where(r => r.Kind == RequestKind.Http))
        {
            var (operation, _) = before.Match(request.Method.ToString(), Resolve(request.Url, collection.Variables));
            if (operation is null)
                continue;
            if (!byOperation.TryGetValue(operation.Key, out var list))
                byOperation[operation.Key] = list = [];
            list.Add(request);
        }

        foreach (var group in report.Changes.GroupBy(c => c.Operation).Where(g => byOperation.ContainsKey(g.Key)))
        {
            var requests = byOperation[group.Key];
            var shapeChanges = new List<ShapeChange>();
            var shapeOrigin = "";
            foreach (var change in group)
            {
                var text = change.ToString();
                switch (change)
                {
                    case { Category: "operation", Description: var d } when d.StartsWith("removed", StringComparison.Ordinal) || d.StartsWith("moved", StringComparison.Ordinal):
                        foreach (var r in requests)
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Breaks, "Request", r.Name, "URL",
                                d.StartsWith("moved", StringComparison.Ordinal) ? $"Calls {group.Key}, which {d}." : $"Calls {group.Key}, which was removed.",
                                d.StartsWith("moved", StringComparison.Ordinal) ? "Update the URL." : null, text));
                        break;
                    case { Category: "parameter", Description: "removed parameter" }:
                        foreach (var r in requests.Where(r => Sends(r, change.Location)))
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Breaks, "Request", r.Name, LocationOf(change.Location),
                                $"Sends {change.Location}, which was removed.", "Remove it from the request.", text));
                        break;
                    case { Category: "parameter", Description: "new required parameter" or "became required" }:
                        foreach (var r in requests.Where(r => !Sends(r, change.Location)))
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Breaks, "Request", r.Name, LocationOf(change.Location),
                                $"Does not send {change.Location}, which is now required.", "Add it to the request.", text));
                        break;
                    case { Category: "parameter" } when change.Description.StartsWith("type ", StringComparison.Ordinal) || change.Description.StartsWith("enum narrowed", StringComparison.Ordinal):
                        foreach (var r in requests.Where(r => Sends(r, change.Location)))
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Possible, "Request", r.Name, LocationOf(change.Location),
                                $"Sends {change.Location}: {change.Description}.", null, text));
                        break;
                    case { Category: "request", Shape: { Kind: ShapeChangeKind.Added } shape } when change.Severity == ChangeSeverity.Breaking:
                        foreach (var r in requests.Where(r => !HasBodyField(r, shape.Path)))
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Breaks, "Request", r.Name, "Body",
                                $"Body lacks {shape.Path}, which is now required.", "Add the field to the body.", text));
                        break;
                    case { Category: "request", Shape: { Kind: ShapeChangeKind.Removed or ShapeChangeKind.Renamed or ShapeChangeKind.TypeChanged } shape }:
                        foreach (var r in requests.Where(r => HasBodyField(r, shape.Path)))
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Possible, "Request", r.Name, "Body",
                                $"Body sends {shape.Path}: {change.Description}.", shape.NewPath is null ? null : $"Rename it to {shape.NewPath}.", text));
                        break;
                    case { Category: "request", Location: "request body", Description: "became required" }:
                        foreach (var r in requests.Where(r => r.Body.Mode == BodyMode.None || string.IsNullOrWhiteSpace(r.Body.Content) && r.Body.FormFields.Count == 0))
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Breaks, "Request", r.Name, "Body", "Sends no body, which is now required.", null, text));
                        break;
                    case { Category: "response", Location: "status" }:
                        var oldStatus = change.Description.Split(' ')[2];
                        foreach (var r in requests)
                        foreach (var a in r.Assertions.Where(a => a.Enabled && a.Source == ValueSource.Status && a.Operator == AssertionOperator.Equals && a.Expected.Trim() == oldStatus))
                            report.Impact.Add(new SpecImpactItem(ImpactSeverity.Breaks, "Assertion", r.Name, "Tests · " + AssertionEvaluator.Describe(a),
                                $"Expects status {oldStatus}: {change.Description}.", $"Change the expected status to {change.Description.Split(' ')[^1]}.", text));
                        break;
                    case { Category: "response", Shape: { Kind: not ShapeChangeKind.Added } shape } when IsSuccessResponse(change.Location):
                        shapeChanges.Add(shape);
                        shapeOrigin = change.Location;
                        break;
                }
            }

            if (shapeChanges.Count == 0)
                continue;
            foreach (var request in requests)
            {
                var impact = ImpactAnalyzer.Analyze(request, shapeChanges, null, collection.Requests, flows, $"{report.OldName}, {shapeOrigin}");
                foreach (var item in impact.Items)
                {
                    var change = group.FirstOrDefault(c => c.Shape is not null && item.Detail.Contains(c.Shape.Path, StringComparison.Ordinal))?.ToString()
                                 ?? $"{group.Key} · {shapeOrigin}";
                    report.Impact.Add(new SpecImpactItem(item.Severity, item.Kind.ToString(), item.Owner, item.Location, item.Detail, item.Suggestion, change));
                }
            }
        }

        var distinct = report.Impact.GroupBy(i => (i.Owner, i.Location, i.Detail)).Select(g => g.OrderBy(i => i.Severity).First())
            .OrderBy(i => i.Severity).ThenBy(i => i.Owner, StringComparer.Ordinal).ToList();
        report.Impact.Clear();
        report.Impact.AddRange(distinct);
    }

    private static bool IsSuccessResponse(string location) =>
        location.StartsWith("response ", StringComparison.Ordinal) && (location["response ".Length..].StartsWith('2') || location["response ".Length..] == "default");

    private static string LocationOf(string parameter) => parameter.Split(' ')[0] switch
    {
        "query" => "Params",
        "header" or "cookie" => "Headers",
        _ => "URL"
    };

    /// <summary>Whether a request sends the parameter "<c>in name</c>" (query in URL or params table, header in headers).</summary>
    private static bool Sends(ApiRequest request, string parameter)
    {
        var parts = parameter.Split(' ', 2);
        if (parts.Length < 2)
            return false;
        var (location, name) = (parts[0], parts[1]);
        return location switch
        {
            "query" => request.QueryParams.Any(p => p.Enabled && p.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                       || QueryString.Parse(request.Url).Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)),
            "header" => request.Headers.Any(h => h.Enabled && h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                        || (request.Auth.Mode == AuthMode.ApiKey && request.Auth.ApiKeyLocation == ApiKeyLocation.Header && request.Auth.ApiKeyName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        || (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && request.Auth.Mode is AuthMode.Bearer or AuthMode.Basic or AuthMode.OAuth2 or AuthMode.Digest),
            "cookie" => request.Headers.Any(h => h.Enabled && h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && h.Value.Contains(name + "=", StringComparison.Ordinal)),
            "path" => true,
            _ => false
        };
    }

    private static bool HasBodyField(ApiRequest request, string path)
    {
        if (request.Body.Mode is BodyMode.FormUrlEncoded or BodyMode.Multipart)
            return path.Count(c => c == '.') == 1 && request.Body.FormFields.Any(f => f.Enabled && "$." + f.Key == path);
        if (request.Body.Mode is not (BodyMode.Json or BodyMode.Text) || string.IsNullOrWhiteSpace(request.Body.Content))
            return false;
        try
        {
            // {{variables}} stand for a value: quoted ones ("{{name}}" → "0") stay strings, bare ones ({{count}} → 0) become numbers.
            var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.RegularExpressions.Regex.Replace(request.Body.Content, @"\{\{[^}]*\}\}", "0"));
            return JsonPath.Select(node, path).Count > 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Resolve(string url, IReadOnlyList<KeyValueItem> variables)
    {
        foreach (var v in variables.Where(v => v.IsActive && v.Value.Length > 0))
            url = url.Replace("{{" + v.Key.Trim() + "}}", v.Value, StringComparison.Ordinal);
        return url;
    }
}
