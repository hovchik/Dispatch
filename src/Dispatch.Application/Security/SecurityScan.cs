using Dispatch.Domain;

namespace Dispatch.Application.Security;

public enum ScanSeverity
{
    Info,
    Low,
    Medium,
    High
}

public enum ScanCategory
{
    TransportSecurity,
    SecurityHeaders,
    InformationDisclosure,
    Injection,
    Authentication,
    InputValidation,
    ErrorHandling,
    Cors
}

/// <summary>One security observation about a request/response.</summary>
public sealed record ScanFinding(
    ScanSeverity Severity,
    ScanCategory Category,
    string Title,
    string Detail,
    string Evidence = "",
    string Remediation = "",
    string RequestName = "")
{
    public string SeverityText => Severity.ToString();
    public string CategoryText => Category switch
    {
        ScanCategory.TransportSecurity => "Transport security",
        ScanCategory.SecurityHeaders => "Security headers",
        ScanCategory.InformationDisclosure => "Information disclosure",
        ScanCategory.InputValidation => "Input validation",
        ScanCategory.ErrorHandling => "Error handling",
        ScanCategory.Cors => "CORS",
        _ => Category.ToString()
    };
}

/// <summary>What a scan probes. Active probes send extra, modified requests; passive checks only read normal responses.</summary>
public sealed class ScanOptions
{
    /// <summary>Passive checks only look at the baseline response; they never send crafted input.</summary>
    public bool Passive { get; init; } = true;

    /// <summary>Active probes send additional requests with test payloads to the same endpoint.</summary>
    public bool Active { get; init; } = true;

    public bool CheckInjection { get; init; } = true;
    public bool CheckAuth { get; init; } = true;
    public bool CheckBoundaries { get; init; } = true;

    /// <summary>Upper bound on extra requests sent per endpoint, so a scan can't hammer a server.</summary>
    public int MaxProbesPerRequest { get; init; } = 40;
}

public sealed class ScanReport
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; set; }
    public int RequestsScanned { get; set; }
    public int ProbesSent { get; set; }
    public List<ScanFinding> Findings { get; } = [];

    public IEnumerable<ScanFinding> Ordered => Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category);
    public int Count(ScanSeverity severity) => Findings.Count(f => f.Severity == severity);
    public bool HasFindingsAtOrAbove(ScanSeverity severity) => Findings.Any(f => f.Severity >= severity);
}
