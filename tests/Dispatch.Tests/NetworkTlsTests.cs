using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Dispatch.Application.Capture;
using Dispatch.Application.Monitoring;
using Dispatch.Application.Reporting;
using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Application.Tls;
using Dispatch.Domain;
using Dispatch.Infrastructure.Capture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

public class NetworkProfileTests
{
    [Theory]
    [InlineData("300ms", 300)]
    [InlineData("1.5s", 1500)]
    [InlineData("2m", 120000)]
    [InlineData("250", 250)]
    public void Parses_durations(string text, double ms) =>
        Assert.Equal(ms, NetworkProfile.ParseDuration(text).TotalMilliseconds);

    [Theory]
    [InlineData("1mbps", 1_000_000)]
    [InlineData("512kbps", 512_000)]
    [InlineData("1.5 Mbit/s", 1_500_000)]
    [InlineData("56k", 56_000)]
    [InlineData("0", 0)]
    [InlineData("unlimited", 0)]
    public void Parses_bandwidth(string text, long bps) =>
        Assert.Equal(bps, NetworkProfile.ParseBandwidth(text));

    [Fact]
    public void Rejects_nonsense()
    {
        Assert.Throws<FormatException>(() => NetworkProfile.ParseDuration("soon"));
        Assert.Throws<FormatException>(() => NetworkProfile.ParseBandwidth("fast"));
        Assert.Throws<FormatException>(() => NetworkProfile.ParsePercent("150%"));
        Assert.Throws<FormatException>(() => NetworkProfile.Build("5g"));
    }

    [Fact]
    public void Preset_values_can_be_overridden()
    {
        var profile = NetworkProfile.Build("3g", loss: "10%");
        Assert.Equal("3g", profile.Name);
        Assert.Equal(300, profile.Latency.TotalMilliseconds);
        Assert.Equal(1_500_000, profile.BandwidthBitsPerSecond);
        Assert.Equal(10, profile.LossPercent);
        Assert.Contains("+300 ms", profile.Describe());
        Assert.Contains("10% loss", profile.Describe());

        var custom = NetworkProfile.Build(null, latency: "50ms", bandwidth: "2mbps");
        Assert.Equal("custom", custom.Name);
        Assert.False(custom.IsNone);
        Assert.True(NetworkProfile.None.IsNone);
        Assert.NotNull(NetworkProfile.FindPreset("Flaky WiFi"));
    }

    [Fact]
    public void Offline_windows_come_at_the_end_of_each_cycle()
    {
        var profile = NetworkProfile.Build("offline"); // offline 10 s every 60 s
        Assert.False(profile.IsOfflineAt(TimeSpan.FromSeconds(5)));
        Assert.False(profile.IsOfflineAt(TimeSpan.FromSeconds(49)));
        Assert.True(profile.IsOfflineAt(TimeSpan.FromSeconds(51)));
        Assert.True(profile.IsOfflineAt(TimeSpan.FromSeconds(59)));
        Assert.False(profile.IsOfflineAt(TimeSpan.FromSeconds(61)));
        Assert.True(profile.IsOfflineAt(TimeSpan.FromSeconds(115)));
        Assert.Equal(45, profile.OnlineRemaining(TimeSpan.FromSeconds(5)).TotalSeconds, 3);
        Assert.Equal(TimeSpan.Zero, profile.OnlineRemaining(TimeSpan.FromSeconds(55)));
    }

    [Fact]
    public void Loss_and_transfer_time_follow_the_profile()
    {
        var profile = NetworkProfile.Build(null, loss: "25%", bandwidth: "8kbps");
        Assert.True(profile.ShouldDrop(0.1));
        Assert.False(profile.ShouldDrop(0.3));
        Assert.Equal(1, profile.TransferTime(1000).TotalSeconds, 3); // 8000 bits at 8 kbps
        Assert.Equal(TimeSpan.Zero, NetworkProfile.None.TransferTime(1000));
    }
}

public class NetworkShaperTests
{
    [Fact]
    public void Counts_drops_and_offline_refusals()
    {
        var shaper = new NetworkShaper(NetworkProfile.Build(null, loss: "100%"), new Random(1));
        var ex = Assert.Throws<NetworkDroppedException>(shaper.Admit);
        Assert.Equal(NetworkDropReason.Loss, ex.Reason);
        Assert.Equal(1, shaper.Dropped);

        // Offline for the whole cycle: always refused.
        var offline = new NetworkShaper(new NetworkProfile("x", TimeSpan.Zero, 0, 0, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Assert.True(offline.IsOffline);
        Assert.Equal(NetworkDropReason.Offline, Assert.Throws<NetworkDroppedException>(offline.Admit).Reason);
        Assert.Equal(1, offline.RefusedOffline);
    }

    [Fact]
    public async Task Paces_writes_to_the_bandwidth_cap()
    {
        var shaper = new NetworkShaper(NetworkProfile.Build(null, bandwidth: "64kbps"));
        using var sink = new MemoryStream();
        var body = new byte[4000]; // 32 kbit → 0.5 s at 64 kbps
        var clock = Stopwatch.StartNew();
        await shaper.WritePacedAsync(sink, body, CancellationToken.None);
        Assert.Equal(body.Length, sink.Length);
        Assert.True(clock.ElapsedMilliseconds >= 400, $"took only {clock.ElapsedMilliseconds} ms");
        Assert.Equal(1, shaper.Paced);
    }
}

public class NetworkConditionsProxyTests
{
    private static HttpClient ProxyClient(int port) =>
        new(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };

    [Fact]
    public async Task Latency_profile_delays_the_response()
    {
        await using var origin = await TestServer.StartAsync(app => app.MapGet("/x", () => "ok"));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0, Network = NetworkProfile.Build(null, latency: "400ms") });

        using var client = ProxyClient(proxy.Port);
        var clock = Stopwatch.StartNew();
        var text = await client.GetStringAsync($"{origin.BaseUrl}/x");
        Assert.Equal("ok", text);
        Assert.True(clock.ElapsedMilliseconds >= 350, $"took only {clock.ElapsedMilliseconds} ms");
        Assert.Equal(1, proxy.Shaper!.Delayed);
    }

    [Fact]
    public async Task Full_loss_drops_the_connection_and_records_the_drop()
    {
        await using var origin = await TestServer.StartAsync(app => app.MapGet("/x", () => "ok"));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        var captured = new List<CapturedExchange>();
        proxy.Captured += captured.Add;
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0, Network = NetworkProfile.Build(null, loss: "100%") });

        using var client = ProxyClient(proxy.Port);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync($"{origin.BaseUrl}/x"));
        // HttpClient may retry a GET whose connection died before any response arrived, so one call can mean two drops.
        Assert.True(proxy.Shaper!.Dropped >= 1);
        Assert.NotEmpty(captured);
        Assert.All(captured, e => Assert.Contains("packet loss", e.Error));
    }

    [Fact]
    public async Task Offline_window_refuses_requests()
    {
        await using var origin = await TestServer.StartAsync(app => app.MapGet("/x", () => "ok"));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        // Offline for the whole cycle, so the first request already falls into the window.
        await proxy.StartAsync(new CaptureProxyOptions
        {
            Port = 0, Network = new NetworkProfile("always-off", TimeSpan.Zero, 0, 0, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))
        });

        using var client = ProxyClient(proxy.Port);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync($"{origin.BaseUrl}/x"));
        Assert.True(proxy.Shaper!.RefusedOffline >= 1);
    }

    [Fact]
    public async Task Bandwidth_cap_slows_the_download()
    {
        var body = new string('x', 8000); // 64 kbit → 1 s at 64 kbps
        await using var origin = await TestServer.StartAsync(app => app.MapGet("/big", () => body));
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0, Network = NetworkProfile.Build(null, bandwidth: "64kbps") });

        using var client = ProxyClient(proxy.Port);
        var clock = Stopwatch.StartNew();
        var text = await client.GetStringAsync($"{origin.BaseUrl}/big");
        Assert.Equal(body, text);
        Assert.True(clock.ElapsedMilliseconds >= 800, $"took only {clock.ElapsedMilliseconds} ms");
    }
}

public class TlsInspectorTests
{
    [Theory]
    [InlineData("example.com", "example.com", 443)]
    [InlineData("example.com:8443", "example.com", 8443)]
    [InlineData("https://example.com/path?x=1", "example.com", 443)]
    [InlineData("https://example.com:9443/", "example.com", 9443)]
    [InlineData("[::1]:8443", "::1", 8443)]
    public void Parses_targets(string text, string host, int port)
    {
        Assert.True(TlsInspector.TryParseTarget(text, 443, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Fact]
    public void Rejects_bad_targets()
    {
        Assert.False(TlsInspector.TryParseTarget("", 443, out _, out _));
        Assert.False(TlsInspector.TryParseTarget("not a host", 443, out _, out _));
    }

    [Fact]
    public async Task Reports_the_self_signed_test_server()
    {
        await using var tls = await TlsTestServer.StartAsync();
        var report = await new TlsInspector().InspectAsync("localhost", tls.Port, new TlsInspectOptions { WarnDays = 21, CheckRevocation = false });

        Assert.Null(report.Error);
        Assert.StartsWith("TLS 1.", report.Protocol);
        Assert.NotEmpty(report.CipherSuite);
        var leaf = Assert.IsType<TlsCertificateInfo>(report.Leaf);
        Assert.Equal("localhost", leaf.CommonName);
        Assert.Contains("localhost", leaf.SubjectAlternativeNames);
        Assert.True(leaf.IsSelfSigned);
        Assert.True(report.HostnameMatches);
        Assert.False(report.ChainTrusted);
        Assert.InRange(report.DaysToExpiry!.Value, 300, 370);
        Assert.Equal("not checked", report.Revocation);
        Assert.Equal(TlsVerdict.Bad, report.Verdict);
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("self-signed"));
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Good && i.Text.Contains("valid for another"));
    }

    [Fact]
    public async Task Warns_when_the_certificate_expires_within_the_threshold()
    {
        await using var tls = await TlsTestServer.StartAsync();
        var report = await new TlsInspector().InspectAsync("localhost", tls.Port, new TlsInspectOptions { WarnDays = 400, CheckRevocation = false });
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Warning && i.Text.Contains("expires in"));
        Assert.Contains(report.Problems, p => p.Contains("expires in"));
    }

    [Fact]
    public async Task Hostname_mismatch_is_reported()
    {
        await using var tls = await TlsTestServer.StartAsync();
        var report = await new TlsInspector().InspectAsync("127.0.0.1", tls.Port,
            new TlsInspectOptions { ServerName = "api.example.com", CheckRevocation = false });
        Assert.Null(report.Error);
        Assert.False(report.HostnameMatches);
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("not valid for api.example.com"));
    }

    [Fact]
    public async Task A_plain_http_port_gives_an_error_report()
    {
        await using var origin = await TestServer.StartAsync(app => app.MapGet("/", () => "plain"));
        var port = new Uri(origin.BaseUrl).Port;
        var report = await new TlsInspector().InspectAsync("127.0.0.1", port, new TlsInspectOptions { Timeout = TimeSpan.FromSeconds(5) });
        Assert.NotNull(report.Error);
        Assert.Equal(TlsVerdict.Bad, report.Verdict);
        Assert.Contains(report.Insights, i => i.Text.Contains("No TLS handshake"));
    }

    [Fact]
    public async Task Writers_render_text_html_and_json()
    {
        await using var tls = await TlsTestServer.StartAsync();
        var report = await new TlsInspector().InspectAsync("localhost", tls.Port, new TlsInspectOptions { CheckRevocation = false });

        var text = TlsReportWriter.Text(report);
        Assert.Contains($"TLS · localhost:{tls.Port}", text);
        Assert.Contains("chain", text);
        var html = TlsReportWriter.Html(report);
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("Certificate chain", html);
        var json = JsonNode.Parse(TlsReportWriter.Json(report))!;
        Assert.False(json["passed"]!.GetValue<bool>());
        var host = json["hosts"]![0]!;
        Assert.Equal("localhost", host["host"]!.GetValue<string>());
        Assert.Equal("Bad", host["verdict"]!.GetValue<string>());
        Assert.Equal("localhost", host["chain"]![0]!["commonName"]!.GetValue<string>());
    }

    [Fact]
    public void Collection_targets_are_distinct_https_hosts_with_variables_resolved()
    {
        var collection = new RequestCollection
        {
            Name = "C",
            Variables = [new KeyValueItem("baseUrl", "https://api.example.com")],
            Requests =
            [
                new ApiRequest { Name = "A", Url = "{{baseUrl}}/users" },
                new ApiRequest { Name = "B", Url = "{{baseUrl}}/orders" },
                new ApiRequest { Name = "C", Url = "https://auth.example.com:8443/token" },
                new ApiRequest { Name = "D", Url = "http://plain.example.com/" },
                new ApiRequest { Name = "E", Url = "{{missing}}/x" }
            ]
        };
        var targets = TlsTargets.FromCollection(collection);
        Assert.Equal(2, targets.Count);
        Assert.Equal(["A", "B"], targets[0].RequestNames);
        Assert.Equal("api.example.com", targets[0].Display);
        Assert.Equal("auth.example.com:8443", targets[1].Display);
    }
}

public class MonitorCertificateTests
{
    private sealed class FakeTls(Func<string, TlsReport> respond) : ITlsInspector
    {
        public List<string> Hosts { get; } = [];

        public Task<TlsReport> InspectAsync(string host, int port, TlsInspectOptions? options = null, CancellationToken ct = default)
        {
            Hosts.Add($"{host}:{port}");
            return Task.FromResult(respond(host));
        }
    }

    private static MonitorService Build(FakeTls tls) =>
        new(new CollectionRunner(new RequestSender([new FakeExecutor(r => new ApiResponse { StatusCode = 200, Succeeded = true, EffectiveUrl = r.Url })], new NullHistory())),
            new RecordingAlertSender(), tls);

    private static MonitorContext Context() => new(new RequestCollection
    {
        Name = "C",
        Requests =
        [
            new ApiRequest { Name = "Ping", Url = "https://api.example.com/ping" },
            new ApiRequest { Name = "Plain", Url = "http://plain.example.com/ping" }
        ]
    }, null);

    [Fact]
    public async Task Expiring_certificate_fails_the_run()
    {
        var tls = new FakeTls(_ => new TlsReport { Host = "api.example.com", ChainTrusted = true, HostnameMatches = true, Protocol = "TLS 1.3", DaysToExpiry = 5 });
        var run = await Build(tls).RunOnceAsync(new MonitorDefinition { Name = "M", CertificateWarnDays = 14 }, Context(), null);
        Assert.False(run.Passed);
        Assert.Contains("expires in 5 day(s)", run.Summary);
        Assert.Equal(["api.example.com:443"], tls.Hosts); // HTTP hosts are not inspected
    }

    [Fact]
    public async Task Healthy_certificate_passes_and_the_check_is_skipped_when_off()
    {
        var tls = new FakeTls(_ => new TlsReport { Host = "api.example.com", ChainTrusted = true, HostnameMatches = true, DaysToExpiry = 90 });
        var run = await Build(tls).RunOnceAsync(new MonitorDefinition { Name = "M", CertificateWarnDays = 14 }, Context(), null);
        Assert.True(run.Passed);
        Assert.Equal("All checks passed.", run.Summary);

        var off = new FakeTls(_ => throw new InvalidOperationException("should not be called"));
        var offRun = await Build(off).RunOnceAsync(new MonitorDefinition { Name = "M", CertificateWarnDays = 0 }, Context(), null);
        Assert.True(offRun.Passed);
        Assert.Empty(off.Hosts);
    }

    [Fact]
    public async Task Handshake_failure_is_reported()
    {
        var tls = new FakeTls(_ => new TlsReport { Host = "api.example.com", Error = "Connection refused" });
        var run = await Build(tls).RunOnceAsync(new MonitorDefinition { Name = "M", CertificateWarnDays = 7 }, Context(), null);
        Assert.False(run.Passed);
        Assert.Contains("TLS handshake with api.example.com failed", run.Summary);
    }

    [Fact]
    public void Cloning_keeps_the_threshold() =>
        Assert.Equal(30, new MonitorDefinition { CertificateWarnDays = 30 }.Clone().CertificateWarnDays);
}

[Collection("Console")]
public class CliTlsAndNetworkTests
{
    [Fact]
    public async Task Tls_command_inspects_a_host_and_fails_on_problems()
    {
        await using var tls = await TlsTestServer.StartAsync();
        var dir = Cli.TempDir();
        var (exit, output) = await Cli.RunAsync("tls", $"localhost:{tls.Port}", "--no-revocation", "-r", "cli,json,html", "-o", dir);

        Assert.Equal(1, exit); // self-signed → Bad
        Assert.Contains($"TLS · localhost:{tls.Port}", output);
        Assert.Contains("self-signed", output);
        var json = Assert.Single(Directory.GetFiles(dir, "*.json"));
        Assert.Contains("\"commonName\": \"localhost\"", await File.ReadAllTextAsync(json));
        Assert.Single(Directory.GetFiles(dir, "*.html"));
    }

    [Fact]
    public async Task Tls_command_rejects_usage_errors()
    {
        var (exit, output) = await Cli.RunAsync("tls");
        Assert.Equal(2, exit);
        Assert.Contains("Usage: dispatch tls", output);

        var (badReporter, text) = await Cli.RunAsync("tls", "example.com", "-r", "pdf");
        Assert.Equal(2, badReporter);
        Assert.Contains("Unknown reporter", text);
    }

    [Fact]
    public async Task Capture_rejects_bad_network_options()
    {
        var (exit, output) = await Cli.RunAsync("capture", "--port", "0", "--network", "5g");
        Assert.Equal(2, exit);
        Assert.Contains("Unknown network profile", output);

        var (exit2, output2) = await Cli.RunAsync("capture", "--port", "0", "--latency", "slow");
        Assert.Equal(2, exit2);
        Assert.Contains("is not a latency", output2);
    }
}
