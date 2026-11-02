using Dispatch.Application.Webhooks;
using Dispatch.Infrastructure.Webhooks;

namespace Dispatch.Cli;

/// <summary><c>dispatch webhooks</c>: run the webhook receiver from the terminal and save what it records.</summary>
public static class WebhooksCommand
{
    private const string Usage = "Usage: dispatch webhooks [--port 9000] [--public] [--out hooks.json] [--duration 10m] [--expect n] [--path /hooks/orders] " +
                                 "[--status 200] [--body '{\"ok\":true}'] [--delay 0ms] [-r cli,html,json] [--report-dir dir] [--quiet] [--no-color]";

    private static readonly HashSet<string> Flags = ["public", "quiet", "no-color", "help"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["p"] = "port", ["o"] = "out", ["r"] = "reporter" });
        if (args.Flag("help") || args.Positionals.Count > 0)
            throw new UsageException(Usage);
        SingleRequest.ValidateReporters(args);
        var reporters = args.Options("reporter").Select(r => r.ToLowerInvariant()).ToList();
        var outPath = args.Option("out");
        var expect = args.Int("expect", 0);
        var pathFilter = args.Option("path") ?? "";
        var duration = args.Duration("duration", TimeSpan.Zero);
        var status = args.Int("status", 200);
        if (status is < 100 or > 599)
            throw new UsageException("--status must be an HTTP status code (100-599).");
        var quiet = args.Flag("quiet");
        var color = SingleRequest.Color(args);

        await using var receiver = new WebhookReceiver();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var matched = 0;
        receiver.Log.Added += hook =>
        {
            var matches = WebhookMatcher.Matches(hook, pathFilter);
            if (!quiet)
                Console.WriteLine((matches ? Paint("←", "32", color) : Paint("·", "90", color)) + WebhookReportWriter.Line(hook));
            if (matches && expect > 0 && Interlocked.Increment(ref matched) >= expect)
                stop.Cancel();
        };

        await receiver.StartAsync(new WebhookReceiverOptions
        {
            Port = args.Option("port") is null ? WebhookReceiverOptions.DefaultPort : args.Int("port", 9000),
            Public = args.Flag("public"),
            ResponseStatus = status,
            ResponseBody = args.Option("body") ?? """{"received":true}""",
            ResponseContentType = args.Option("content-type") ?? "application/json",
            ResponseDelayMs = (int)args.Duration("delay", TimeSpan.Zero).TotalMilliseconds
        }, cancellationToken);

        Console.WriteLine($"Webhook receiver listening on {receiver.BaseUrl} (any method, any path)" +
                          (args.Flag("public") ? " on all interfaces" : "") + $" · answers {status}");
        if (pathFilter.Length > 0)
            Console.WriteLine($"Counting callbacks to {pathFilter}" + (expect > 0 ? $"; stopping after {expect}." : "."));
        else if (expect > 0)
            Console.WriteLine($"Stopping after {expect} callback(s).");
        Console.WriteLine("To receive callbacks from the internet, expose the port with a tunnel, e.g. " +
                          $"`cloudflared tunnel --url http://localhost:{receiver.Port}` or `ngrok http {receiver.Port}`.");
        Console.WriteLine(duration > TimeSpan.Zero ? $"Press Ctrl+C to stop early (runs for {duration.TotalSeconds:0} s)." : "Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(duration > TimeSpan.Zero ? duration : Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        var hooks = receiver.Log.Snapshot();
        var baseUrl = receiver.BaseUrl;
        await receiver.StopAsync();

        var hits = pathFilter.Length == 0 ? hooks.Count : hooks.Count(h => WebhookMatcher.Matches(h, pathFilter));
        Console.WriteLine();
        Console.WriteLine($"Received {hooks.Count} callback(s)" + (pathFilter.Length > 0 ? $", {hits} to {pathFilter}." : "."));
        if (reporters.Contains("cli") && hooks.Count > 0)
            Console.WriteLine(WebhookReportWriter.Text(hooks, baseUrl));

        if (outPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            await File.WriteAllTextAsync(outPath, WebhookReportWriter.Json(hooks, baseUrl), CancellationToken.None);
            Console.WriteLine($"Saved {hooks.Count} hook(s) to {Path.GetFullPath(outPath)}");
        }
        var reportDir = args.Option("report-dir") ?? "dispatch-reports";
        var baseName = $"webhooks-{DateTime.Now:yyyyMMdd-HHmmss}";
        foreach (var reporter in reporters.Where(r => r != "cli").Distinct())
        {
            var (ext, content) = reporter == "html" ? (".html", WebhookReportWriter.Html(hooks, baseUrl)) : (".json", WebhookReportWriter.Json(hooks, baseUrl));
            Directory.CreateDirectory(reportDir);
            var path = Path.Combine(reportDir, baseName + ext);
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
            Console.WriteLine($"{reporter} report: {Path.GetFullPath(path)}");
        }

        return expect > 0 && hits < expect ? Program.ExitTestsFailed : Program.ExitOk;
    }

    private static string Paint(string text, string code, bool color) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
