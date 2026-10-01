using System.Text;
using Dispatch.Application.Requests;
using Dispatch.Application.Security;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class PassiveCheckTests
{
    private static ApiResponse Response(int status, string body, string? contentType, params (string, string)[] headers) => new()
    {
        StatusCode = status,
        Succeeded = status < 400,
        Body = body,
        ContentType = contentType,
        Headers = headers.Select(h => new ResponseHeader(h.Item1, h.Item2)).ToList(),
        EffectiveUrl = "https://api.example.com/x"
    };

    [Fact]
    public void Flags_missing_headers_banners_and_insecure_cookies()
    {
        var request = new ApiRequest { Name = "Home", Url = "https://api.example.com/x" };
        var findings = PassiveChecks.Inspect(request, Response(200, "<html><body>hi</body></html>", "text/html",
            ("Server", "Apache/2.4.49"), ("Set-Cookie", "sessionId=abc; Path=/"),
            ("Access-Control-Allow-Origin", "*"), ("Access-Control-Allow-Credentials", "true"))).ToList();

        Assert.Contains(findings, f => f.Title.Contains("X-Content-Type-Options"));
        Assert.Contains(findings, f => f.Title.Contains("Content-Security-Policy"));
        Assert.Contains(findings, f => f.Category == ScanCategory.InformationDisclosure && f.Evidence.Contains("Apache/2.4.49"));
        Assert.Contains(findings, f => f.Title.Contains("HttpOnly") && f.Severity == ScanSeverity.Medium);
        Assert.Contains(findings, f => f.Title.Contains("Secure"));
        Assert.Contains(findings, f => f.Category == ScanCategory.Cors && f.Severity == ScanSeverity.High);
    }

    [Fact]
    public void Flags_plaintext_http()
    {
        var request = new ApiRequest { Name = "Insecure", Url = "http://api.example.com/x" };
        var http = new ApiResponse { StatusCode = 200, Succeeded = true, Body = "{}", ContentType = "application/json",
            EffectiveUrl = "http://api.example.com/x" };
        Assert.Contains(PassiveChecks.Inspect(request, http), f => f.Category == ScanCategory.TransportSecurity && f.Severity == ScanSeverity.High);
    }

    [Fact]
    public void Detects_stack_traces_and_leaked_secrets()
    {
        var request = new ApiRequest { Name = "Error", Url = "https://api.example.com/x" };
        var trace = PassiveChecks.Inspect(request, Response(200, "System.NullReferenceException: at Api.Handler.Run() in /srv/app/Handler.cs:line 42", "text/plain",
            ("X-Content-Type-Options", "nosniff"))).ToList();
        Assert.Contains(trace, f => f.Title.Contains("Stack trace"));

        var secret = PassiveChecks.Inspect(request, Response(200, """{"awsKey":"AKIAIOSFODNN7EXAMPLE"}""", "application/json",
            ("X-Content-Type-Options", "nosniff"))).ToList();
        Assert.Contains(secret, f => f.Title.Contains("AWS access key") && f.Severity == ScanSeverity.High);
    }

    [Fact]
    public void Clean_json_api_response_has_only_minor_or_no_findings()
    {
        var request = new ApiRequest { Name = "OK", Url = "https://api.example.com/x" };
        var findings = PassiveChecks.Inspect(request, Response(200, """{"ok":true}""", "application/json",
            ("X-Content-Type-Options", "nosniff"), ("Strict-Transport-Security", "max-age=31536000"))).ToList();
        Assert.DoesNotContain(findings, f => f.Severity >= ScanSeverity.Medium);
    }
}

public class ProbeBuilderTests
{
    [Fact]
    public void Builds_probes_for_query_path_and_json_fields_and_removes_auth()
    {
        var request = new ApiRequest
        {
            Name = "Search", Method = HttpVerb.Post, Url = "https://x/api/users/{{id}}?q=hi",
            QueryParams = [new("q", "hi")],
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"name":"Ann","age":30}""" },
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "t" }
        };
        var probes = Probes.ForRequest(request, new ScanOptions()).ToList();

        Assert.Contains(probes, p => p is { Location: "query", Field: "q" });
        Assert.Contains(probes, p => p.Location == "path");
        Assert.Contains(probes, p => p is { Location: "body", Field: "name" });
        Assert.DoesNotContain(probes, p => p.Field == "age"); // numbers aren't string-mutated
        var authProbe = Assert.Single(probes, p => p.Kind == ProbeKind.AuthBypass);
        Assert.Equal(AuthMode.None, authProbe.Request.Auth.Mode);

        var reflection = probes.First(p => p.Kind == ProbeKind.Reflection && p.Field == "q");
        Assert.Contains(Probes.ReflectionMarker, reflection.Request.Url);
    }

    [Fact]
    public void Respects_the_probe_cap()
    {
        var request = new ApiRequest { Name = "x", Url = "https://x/a?b=1", QueryParams = [new("b", "1")] };
        Assert.True(Probes.ForRequest(request, new ScanOptions { MaxProbesPerRequest = 3 }).Count() <= 3);
    }
}

/// <summary>An intentionally weak in-process API, used only to prove the scanner detects these classes of issue.</summary>
[Collection("Console")]
public class SecurityScannerTests
{
    private static async Task<TestServer> VulnerableServerAsync() => await TestServer.StartAsync(app =>
    {
        // Reflects the q parameter into HTML without encoding (reflected XSS).
        app.MapGet("/search", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query["q"].ToString();
            ctx.Response.ContentType = "text/html";
            return ctx.Response.WriteAsync($"<html><body>Results for {q}</body></html>");
        });
        // Surfaces a SQL-style error when the id contains a quote (missing validation).
        app.MapGet("/items/{id}", (string id) =>
            id.Contains('\'')
                ? Results.Text("You have an error in your SQL syntax near \"'\"", "text/plain", statusCode: 500)
                : Results.Json(new { id }));
        // Numeric endpoint that throws on non-numeric input (no boundary handling).
        app.MapGet("/n", (HttpContext ctx) =>
            int.TryParse(ctx.Request.Query["v"], out var v) ? Results.Json(new { v }) : Results.StatusCode(500));
        // "Protected" endpoint that never actually checks the token.
        app.MapGet("/me", () => Results.Json(new { user = "admin" }));
    });

    private static async Task<(SecurityScanner Scanner, ServiceProvider Services)> ScannerAsync()
    {
        var services = new ServiceCollection().AddDispatchEngine()
            .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>()
            .AddSingleton<SecurityScanner>()
            .BuildServiceProvider();
        return (services.GetRequiredService<SecurityScanner>(), services);
    }

    [Fact]
    public async Task Finds_reflected_xss_sql_error_boundary_and_missing_auth()
    {
        await using var server = await VulnerableServerAsync();
        var (scanner, services) = await ScannerAsync();
        await using var _ = services;

        var requests = new List<ApiRequest>
        {
            new() { Name = "Search", Url = $"{server.BaseUrl}/search?q=test", QueryParams = [new("q", "test")] },
            new() { Name = "Get item", Url = $"{server.BaseUrl}/items/42" },
            new() { Name = "Number", Url = $"{server.BaseUrl}/n?v=5", QueryParams = [new("v", "5")] },
            new() { Name = "Me", Url = $"{server.BaseUrl}/me", Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "secret" } }
        };

        var report = await scanner.ScanAsync(requests, new ScanOptions(), null, cancellationToken: CancellationToken.None);

        Assert.Contains(report.Findings, f => f.Category == ScanCategory.Injection && f.Title.Contains("reflected") && f.RequestName == "Search");
        Assert.Contains(report.Findings, f => f.Category == ScanCategory.Injection && f.Title.Contains("SQL") && f.RequestName == "Get item");
        Assert.Contains(report.Findings, f => f.Category == ScanCategory.InputValidation && f.RequestName == "Number");
        Assert.Contains(report.Findings, f => f.Category == ScanCategory.Authentication && f.RequestName == "Me");
        Assert.True(report.ProbesSent > 0);
        Assert.True(report.HasFindingsAtOrAbove(ScanSeverity.High));
    }

    [Fact]
    public async Task Passive_only_scan_sends_no_probes()
    {
        await using var server = await VulnerableServerAsync();
        var (scanner, services) = await ScannerAsync();
        await using var _ = services;
        var report = await scanner.ScanAsync([new ApiRequest { Name = "Search", Url = $"{server.BaseUrl}/search?q=test", QueryParams = [new("q", "test")] }],
            new ScanOptions { Active = false }, null, cancellationToken: CancellationToken.None);
        Assert.Equal(0, report.ProbesSent);
        Assert.Equal(1, report.RequestsScanned);
    }

    [Fact]
    public void Report_writers_render_text_html_and_json()
    {
        var report = new ScanReport { RequestsScanned = 1 };
        report.Findings.Add(new ScanFinding(ScanSeverity.High, ScanCategory.Injection, "Reflected input", "detail", "evidence", "fix", "Search"));
        Assert.Contains("HIGH", ScanReportWriter.Text(report));
        Assert.Contains("Reflected input", ScanReportWriter.Html(report, "API"));
        Assert.Contains("\"severity\": \"High\"", ScanReportWriter.Json(report));
    }
}

[Collection("Console")]
public class CliScanTests
{
    [Fact]
    public async Task Scan_command_reports_and_fails_on_threshold()
    {
        await using var server = await TestServer.StartAsync(app =>
            app.MapGet("/items/{id}", (string id) => id.Contains('\'')
                ? Results.Text("SQLSTATE[42000]: syntax error", "text/plain", statusCode: 500)
                : Results.Json(new { id })));
        var dir = Cli.TempDir();
        var source = Path.Combine(dir, "api.dispatch.json");
        await File.WriteAllTextAsync(source, Application.Interop.DispatchFormat.ExportCollection(new RequestCollection
        {
            Name = "Vuln", Requests = [new ApiRequest { Name = "Get", Url = $"{server.BaseUrl}/items/1" }]
        }));

        var (exit, output) = await Cli.RunAsync("scan", source, "--db", Path.Combine(dir, "db"), "--no-color", "--fail-on", "high", "--no-auth");
        Assert.Equal(1, exit);
        Assert.Contains("HIGH", output);
        Assert.Contains("SQL", output);
    }
}
