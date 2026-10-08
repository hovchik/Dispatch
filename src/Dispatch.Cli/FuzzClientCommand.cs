using Dispatch.Application.ClientFuzz;
using Dispatch.Infrastructure.Capture;

namespace Dispatch.Cli;

/// <summary><c>dispatch fuzz-client</c>: a proxy that varies responses to find how an app (the client) breaks.</summary>
public static class FuzzClientCommand
{
    private static readonly HashSet<string> Flags = ["public", "no-decrypt", "no-color", "fail-on-break"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["p"] = "port", ["o"] = "out", ["r"] = "reporter" });
        var kinds = args.Options("kinds").Count == 0
            ? Enum.GetValues<MutationKind>().ToHashSet()
            : args.Options("kinds").Select(k => Enum.TryParse<MutationKind>(k, ignoreCase: true, out var kind)
                ? kind
                : throw new UsageException($"--kinds: unknown '{k}'. Use: {string.Join(", ", Enum.GetNames<MutationKind>())}")).ToHashSet();
        var options = new ClientFuzzOptions
        {
            HostFilter = args.Option("host") ?? "",
            Observation = args.Duration("window", TimeSpan.FromSeconds(5)),
            BaselineResponses = Math.Max(1, args.Int("baseline", 2)),
            MaxMutationsPerEndpoint = Math.Max(1, args.Int("max", 25)),
            SlowDelay = args.Duration("slow", TimeSpan.FromSeconds(3)),
            Kinds = kinds
        };

        using var authority = new CertificateAuthority(CertificateAuthority.LoadOrCreate(Dispatch.Infrastructure.DispatchPaths.CaptureCaFile));
        if (args.Option("export-ca") is { } caPath)
        {
            await File.WriteAllTextAsync(caPath, authority.CaCertificatePem, cancellationToken);
            Console.WriteLine($"CA certificate written to {Path.GetFullPath(caPath)} — trust it to fuzz HTTPS apps.");
        }

        var color = !args.Flag("no-color") && !Console.IsOutputRedirected;
        var fuzzer = new ClientFuzzer(options);
        fuzzer.ExperimentStarted += e => Console.WriteLine($"→ {e.Endpoint}: sending {e.Mutation.Description}");
        fuzzer.ExperimentFinished += e => Console.WriteLine(e.Verdict == FuzzVerdict.Breaks
            ? $"  {Paint("✗ BREAKS", "31", color)} {string.Join("; ", e.Signals)}"
            : $"  {Paint("✓ copes", "32", color)} ({e.RequestsAfter} request(s) afterwards)");

        await using var proxy = new CaptureProxy(authority);
        await proxy.StartAsync(new CaptureProxyOptions
        {
            Port = args.Int("port", 8899),
            Public = args.Flag("public"),
            HostFilter = options.HostFilter,
            DecryptHttps = !args.Flag("no-decrypt"),
            Interceptor = fuzzer
        });

        Console.WriteLine($"Client fuzzing proxy on http://127.0.0.1:{proxy.Port}" + (options.HostFilter.Length > 0 ? $" · fuzzing {options.HostFilter}" : ""));
        Console.WriteLine("Point your app at it and use the app as usual: after a few normal responses per endpoint, one response at a time is varied.");
        Console.WriteLine("Only fuzz apps you own or may test. Press Ctrl+C to stop" + (args.Option("duration") is null ? "." : $" (stops after {args.Option("duration")})."));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (args.Option("duration") is not null)
            stop.CancelAfter(args.Duration("duration", TimeSpan.FromMinutes(10)));
        try
        {
            // Close watch windows even when the app goes quiet.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(stop.Token))
                fuzzer.Tick(DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
        }
        await proxy.StopAsync();
        fuzzer.Tick(DateTimeOffset.MaxValue);

        var experiments = fuzzer.Experiments;
        Console.WriteLine();
        Console.WriteLine(ClientFuzzReport.Text(experiments));
        await SingleRequest.WriteReportsAsync(args, $"client-fuzz-{DateTimeOffset.Now:yyyyMMdd-HHmmss}", reporter => reporter switch
        {
            "html" => (".html", ClientFuzzReport.Html(experiments)),
            "json" => (".json", ClientFuzzReport.Json(experiments)),
            _ => null
        });
        return args.Flag("fail-on-break") && experiments.Any(e => e.Verdict == FuzzVerdict.Breaks) ? Program.ExitTestsFailed : Program.ExitOk;
    }

    private static string Paint(string text, string code, bool color) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
