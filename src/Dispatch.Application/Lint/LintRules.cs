using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dispatch.Application.Lint;

public enum LintSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A static check the linter runs over a collection. Ids are stable so they can be configured and suppressed.</summary>
public sealed record LintRule(string Id, string Title, string Description, LintSeverity DefaultSeverity, bool EnabledByDefault = true);

/// <summary>Every rule the collection linter knows. Ids never change; new rules get new ids.</summary>
public static class LintRules
{
    public const string UndefinedVariable = "DP001";
    public const string UnusedVariable = "DP002";
    public const string HardcodedSecret = "DP003";
    public const string NoAssertions = "DP004";
    public const string DuplicateRequest = "DP005";
    public const string DuplicateName = "DP006";
    public const string StaleExample = "DP007";
    public const string MissingDescription = "DP008";
    public const string EmptyUrl = "DP009";
    public const string UnusedExtraction = "DP010";

    public static IReadOnlyList<LintRule> All { get; } =
    [
        new(UndefinedVariable, "Undefined variable",
            "A {{variable}} is used but is not defined by the collection, an environment, an extraction rule or a script.", LintSeverity.Warning),
        new(UnusedVariable, "Unused variable", "A collection variable is never referenced by any request or script.", LintSeverity.Info),
        new(HardcodedSecret, "Hard-coded secret",
            "A credential (token, API key, password, private key) is written literally into a URL, header, body or auth setting instead of a variable.",
            LintSeverity.Error),
        new(NoAssertions, "No assertions", "The request has no enabled assertion, test script check or message expectation, so a run can never fail on it.",
            LintSeverity.Warning),
        new(DuplicateRequest, "Duplicate request", "Two requests send the same method, URL and body.", LintSeverity.Warning),
        new(DuplicateName, "Duplicate name", "Two requests in the same folder share a name, which makes --request filters and flows ambiguous.", LintSeverity.Info),
        new(StaleExample, "Stale example",
            "A saved example no longer matches its request: it matches query parameters the request does not send, its body is not the JSON its content type promises, or its schema and body disagree.",
            LintSeverity.Warning),
        new(MissingDescription, "Missing description", "The collection or request has no description (used by the generated docs).", LintSeverity.Info,
            EnabledByDefault: false),
        new(EmptyUrl, "Empty URL", "The request has no URL or endpoint.", LintSeverity.Error),
        new(UnusedExtraction, "Unused extraction", "An extraction rule sets a variable that nothing in the collection uses.", LintSeverity.Info)
    ];

    public static LintRule? Find(string id) => All.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Per-collection rule configuration: a rule can be switched off or given another severity. Serialised as
/// <c>{"rules":{"DP004":"off","DP008":"warning"}}</c>, the format of <c>dispatch-lint.json</c> and of the app's saved settings.
/// </summary>
public sealed class LintConfig
{
    public const string FileName = "dispatch-lint.json";

    /// <summary>Rule id → severity, or null when the rule is switched off. Rules not listed use their defaults.</summary>
    public Dictionary<string, LintSeverity?> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(LintRule rule) => Rules.TryGetValue(rule.Id, out var severity) ? severity is not null : rule.EnabledByDefault;

    public LintSeverity SeverityOf(LintRule rule) => Rules.TryGetValue(rule.Id, out var severity) && severity is not null ? severity.Value : rule.DefaultSeverity;

    public LintConfig Disable(string ruleId)
    {
        Rules[ruleId] = null;
        return this;
    }

    public LintConfig Enable(string ruleId)
    {
        if (LintRules.Find(ruleId) is { } rule)
            Rules[ruleId] = rule.DefaultSeverity;
        return this;
    }

    public LintConfig Set(string ruleId, LintSeverity severity)
    {
        Rules[ruleId] = severity;
        return this;
    }

    /// <summary>Parses "error", "warning", "warn", "info" or "off" / "none" / "false".</summary>
    public static LintSeverity? ParseSeverity(string text) => text.Trim().ToLowerInvariant() switch
    {
        "error" or "err" => LintSeverity.Error,
        "warning" or "warn" => LintSeverity.Warning,
        "info" or "information" or "hint" => LintSeverity.Info,
        "off" or "none" or "false" or "disabled" => null,
        _ => throw new FormatException($"'{text}' is not a lint severity (error, warning, info or off).")
    };

    public static LintConfig Parse(string json)
    {
        var config = new LintConfig();
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A lint config is a JSON object.");
        var rules = root["rules"] as JsonObject ?? root;
        foreach (var (id, value) in rules)
        {
            if (id == "rules" || LintRules.Find(id) is null)
                continue;
            config.Rules[id] = value switch
            {
                null => null,
                JsonValue v when v.TryGetValue<bool>(out var enabled) => enabled ? LintRules.Find(id)!.DefaultSeverity : null,
                _ => ParseSeverity(value.ToString())
            };
        }
        return config;
    }

    public string ToJson()
    {
        var rules = new JsonObject();
        foreach (var (id, severity) in Rules.OrderBy(r => r.Key, StringComparer.Ordinal))
            rules[id] = severity is null ? "off" : severity.Value.ToString().ToLowerInvariant();
        return new JsonObject { ["rules"] = rules }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
