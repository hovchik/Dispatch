using Dispatch.Application.Interop;
using Dispatch.Application.Tls;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary><c>dispatch tls</c>: inspect the TLS endpoint of a host, or of every HTTPS host a collection uses.</summary>
public static class TlsCommand
{
    private const string Usage = "Usage: dispatch tls <host[:port]|collection> [--warn-days 21] [--port 443] [--env e] [--timeout 10s] " +
                                 "[--sni name] [--no-revocation] [-r cli,html,json] [-o dir]";

    private static readonly HashSet<string> Flags = ["no-revocation", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        SingleRequest.ValidateReporters(args);
        if (args.Positionals.Count != 1)
            throw new UsageException(Usage);

        var options = new TlsInspectOptions
        {
            DefaultPort = args.Int("port", 443),
            WarnDays = args.Int("warn-days", 21),
            Timeout = args.Duration("timeout", TimeSpan.FromSeconds(10)),
            CheckRevocation = !args.Flag("no-revocation"),
            ServerName = args.Option("sni")
        };

        var targets = await ResolveTargetsAsync(args, options.DefaultPort);
        var inspector = new TlsInspector();
        var reports = new List<TlsReport>();
        foreach (var target in targets)
        {
            Console.Error.WriteLine($"Inspecting {target.Display}…");
            reports.Add(await inspector.InspectAsync(target.Host, target.Port, options, cancellationToken));
        }

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(TlsReportWriter.Text(reports));
        var baseName = targets.Count == 1 ? DispatchFormat.Slug(targets[0].Display) : "tls";
        await SingleRequest.WriteReportsAsync(args, $"{baseName}-tls-{DateTimeOffset.Now:yyyyMMdd-HHmmss}", reporter => reporter switch
        {
            "html" => (".html", TlsReportWriter.Html(reports)),
            "json" => (".json", TlsReportWriter.Json(reports)),
            _ => null
        });
        return reports.All(r => r.Passed) ? Program.ExitOk : Program.ExitTestsFailed;
    }

    /// <summary>A literal host[:port] wins; anything else is loaded as a collection and its HTTPS hosts are inspected.</summary>
    private static async Task<IReadOnlyList<TlsTarget>> ResolveTargetsAsync(Arguments args, int defaultPort)
    {
        var source = args.Positionals[0];
        var looksLikeCollection = File.Exists(source) || Directory.Exists(source) || source.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                  || source.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || source.Contains(' ');
        if (!looksLikeCollection && TlsInspector.TryParseTarget(source, defaultPort, out var host, out var port))
            return [new TlsTarget(host, port, [])];

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, bundled) = await workspace.LoadCollectionAsync(source, args.Option("collection"));
        var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);
        var targets = TlsTargets.FromCollection(collection, environment);
        if (targets.Count == 0)
            throw new UsageException($"{collection.Name} has no HTTPS requests to inspect.");
        return targets;
    }
}
