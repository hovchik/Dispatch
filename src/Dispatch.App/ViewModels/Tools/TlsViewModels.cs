using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Interop;
using Dispatch.Application.Reporting;
using Dispatch.Application.Tls;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>One certificate row of the chain list.</summary>
public sealed record TlsChainRow(int Index, TlsCertificateInfo Certificate)
{
    public string Number => Index.ToString();
    public string Name => Certificate.CommonName;
    public string Issuer => $"issued by {Certificate.IssuerName}";
    public string Validity => $"{Certificate.NotBefore:yyyy-MM-dd} → {Certificate.NotAfter:yyyy-MM-dd}";
    public string Details => $"{Certificate.PublicKey} · {Certificate.SignatureAlgorithm}" +
                             (Certificate.IsSelfSigned ? " · self-signed" : "") + (Certificate.IsCertificateAuthority ? " · CA" : "");
    public string Sans => Certificate.SubjectAlternativeNames.Count == 0 ? "" : "SAN: " + string.Join(", ", Certificate.SubjectAlternativeNames);
    public bool HasSans => Certificate.SubjectAlternativeNames.Count > 0;
    public string Fingerprint => $"Serial {Certificate.SerialNumber} · SHA-1 {Certificate.Thumbprint}";
}

/// <summary>Inspects the TLS endpoint of a host: chain, expiry, protocol, cipher, ALPN and problems.</summary>
public sealed partial class TlsViewModel : ObservableObject, ITool
{
    private readonly ITlsInspector _inspector;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private CancellationTokenSource? _cts;
    private TlsReport? _report;

    public TlsViewModel(ITlsInspector inspector, IDialogService dialogs, IClipboardService clipboard, string? target = null)
    {
        _inspector = inspector;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _target = target ?? "";
    }

    public string Title => "TLS inspector";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Tls;
    public double Width => 1000;
    public double Height => 700;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InspectCommand))]
    private string _target = "";

    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private decimal _warnDays = 21;
    [ObservableProperty] private bool _checkRevocation = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InspectCommand), nameof(StopCommand), nameof(ExportCommand), nameof(CopyCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Enter a host (api.example.com, host:8443 or a URL) and click Inspect. Only one handshake is made; no request is sent.";
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private IBrush _verdictBrush = ReportBrushes.Neutral;
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CopyCommand))]
    private bool _hasResult;

    public bool HasReport => HasResult;
    public ObservableCollection<StatTile> Stats { get; } = [];
    public ObservableCollection<TlsChainRow> Chain { get; } = [];
    public ObservableCollection<ReportInsight> Insights { get; } = [];

    private bool CanInspect() => !IsRunning && Target.Trim().Length > 0;
    private bool CanStop() => IsRunning;
    private bool CanExport() => !IsRunning && HasResult;

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectAsync()
    {
        if (!TlsInspector.TryParseTarget(Target, 443, out var host, out var port))
        {
            Status = $"'{Target}' is not a host, host:port or URL.";
            return;
        }
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Stats.Clear();
        Chain.Clear();
        Insights.Clear();
        Verdict = "";
        VerdictBrush = ReportBrushes.Neutral;
        Summary = "";
        Status = $"Connecting to {host}:{port}…";
        try
        {
            var report = await _inspector.InspectAsync(host, port, new TlsInspectOptions
            {
                WarnDays = (int)Math.Clamp(WarnDays, 0, 3650),
                CheckRevocation = CheckRevocation,
                ServerName = string.IsNullOrWhiteSpace(ServerName) ? null : ServerName.Trim()
            }, _cts.Token);
            Show(report);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Show(TlsReport report)
    {
        _report = report;
        HasResult = true;
        Verdict = report.Verdict switch { TlsVerdict.Ok => "OK", TlsVerdict.Warning => "WARNING", _ => "PROBLEM" };
        VerdictBrush = report.Verdict switch { TlsVerdict.Ok => ReportBrushes.Good, TlsVerdict.Warning => ReportBrushes.Warning, _ => ReportBrushes.Bad };
        Summary = report.Summary;
        Status = report.Error is null
            ? $"Handshake with {report.Host}:{report.Port} took {report.HandshakeTime?.TotalMilliseconds ?? report.Duration.TotalMilliseconds:0} ms."
            : $"Could not complete a TLS handshake with {report.Host}:{report.Port}.";
        if (report.Error is null)
        {
            Stats.Add(new StatTile("Protocol", report.Protocol, null, report.Protocol is "TLS 1.3" or "TLS 1.2" ? null : ReportBrushes.Bad));
            Stats.Add(new StatTile("Cipher", report.CipherSuite));
            Stats.Add(new StatTile("ALPN", report.Alpn.Length > 0 ? report.Alpn : "none", report.Alpn == "h2" ? "HTTP/2 available" : null));
            Stats.Add(new StatTile("Expires in", report.DaysToExpiry is { } d ? $"{d:0} days" : "—",
                report.Leaf?.NotAfter.ToString("yyyy-MM-dd"),
                report.DaysToExpiry is { } dd ? dd < 0 ? ReportBrushes.Bad : dd <= report.WarnDays ? ReportBrushes.Warning : ReportBrushes.Good : null));
            Stats.Add(new StatTile("Trusted", report.ChainTrusted ? "yes" : "no", null, report.ChainTrusted ? ReportBrushes.Good : ReportBrushes.Bad));
            Stats.Add(new StatTile("Hostname", report.HostnameMatches ? "matches" : "mismatch", report.ServerName,
                report.HostnameMatches ? ReportBrushes.Good : ReportBrushes.Bad));
            Stats.Add(new StatTile("Revocation", report.Revocation, null,
                report.Revocation == "revoked" ? ReportBrushes.Bad : report.Revocation == "good" ? ReportBrushes.Good : null));
        }
        for (var i = 0; i < report.Chain.Count; i++)
            Chain.Add(new TlsChainRow(i + 1, report.Chain[i]));
        foreach (var insight in report.Insights)
            Insights.Add(insight);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", TlsReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", TlsReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save TLS report", DispatchFormat.Slug($"{_report.Host}-{_report.Port}") + "-tls" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task CopyAsync()
    {
        if (_report is null)
            return;
        await _clipboard.SetTextAsync(TlsReportWriter.Text(_report));
        Status = "Report copied as text.";
    }

    public void OnClosed() => _cts?.Cancel();
}
