using System.Globalization;
using Dispatch.Application.Load;
using Dispatch.Infrastructure.Mock;
using Dispatch.Infrastructure.Protocols.Grpc;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

public static class MockCommand
{
    private static readonly HashSet<string> Flags = ["public", "no-cors", "dynamic", "stateful"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["p"] = "port" });
        if (args.Positionals.Count != 1)
            throw new UsageException("Usage: dispatch mock <collection> [--port 3000] [--grpc-port 50051] [--latency 100] [--jitter 50] " +
                                     "[--error-rate 0.1] [--error-status 503] [--drop-rate 0.05] [--public] [--no-cors] [--dynamic] [--stateful] [--seed n] " +
                                     "[--session-speed 1]");

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, _) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));

        await using var server = new MockServer(services.GetRequiredService<GrpcSchemaProvider>());
        server.RequestHandled += entry =>
            Console.WriteLine($"{entry.Time:HH:mm:ss} {entry.Method,-7} {entry.Path}  → {entry.Status}  {entry.Milliseconds:0} ms" +
                              (entry.Matched is null ? "" : $"  [{entry.Matched}]") + (entry.Note is null ? "" : $"  ({entry.Note})"));

        await server.StartAsync(Program.Filter(collection, args), new MockServerOptions
        {
            Port = args.Int("port", 3000),
            GrpcPort = args.Option("grpc-port") is null ? null : args.Int("grpc-port", 50051),
            Public = args.Flag("public"),
            LatencyMs = args.Int("latency", 0),
            LatencyJitterMs = args.Int("jitter", 0),
            ErrorRate = Rate(args, "error-rate"),
            ErrorStatus = args.Int("error-status", 500),
            DropRate = Rate(args, "drop-rate"),
            Cors = !args.Flag("no-cors"),
            DynamicData = args.Flag("dynamic"),
            Stateful = args.Flag("stateful"),
            Seed = args.Option("seed") is null ? null : args.Int("seed", 0),
            SessionSpeed = SessionSpeed(args)
        }, cancellationToken);

        Console.WriteLine($"Mocking {collection.Name} at {server.BaseUrl}" + (server.GrpcUrl is null ? "" : $" (gRPC at {server.GrpcUrl})") +
                          (args.Flag("dynamic") ? " · dynamic data" : "") + (args.Flag("stateful") ? " · stateful" : ""));
        foreach (var route in server.Routes)
            Console.WriteLine($"  {route.Method,-7} {route.Template}  ({route.Request.Examples.Count} example(s))");
        foreach (var route in server.GrpcRoutes)
            Console.WriteLine($"  gRPC    {route}");
        foreach (var warning in server.Warnings)
            Console.Error.WriteLine($"warning: {warning}");
        Console.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        await server.StopAsync();
        return Program.ExitOk;
    }

    /// <summary>--session-speed: 1 = recorded timing, 2 = twice as fast, 0 = no delays.</summary>
    private static double SessionSpeed(Arguments args)
    {
        var text = args.Option("session-speed");
        if (text is null)
            return 1;
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed) && speed >= 0
            ? speed
            : throw new UsageException("--session-speed must be a number ≥ 0 (1 = recorded timing, 0 = no delays).");
    }

    private static double Rate(Arguments args, string name)
    {
        var text = args.Option(name);
        if (text is null)
            return 0;
        if (!double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new UsageException($"--{name} must be a number between 0 and 1 (or a percentage).");
        if (text.EndsWith('%') || value > 1)
            value /= 100;
        return Math.Clamp(value, 0, 1);
    }
}

public static class LoadCommand
{
    private static readonly HashSet<string> Flags = ["no-checks", "insecure"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["u"] = "users", ["t"] = "duration", ["e"] = "env" });
        if (args.Positionals.Count != 1)
            throw new UsageException("Usage: dispatch load <collection> [--users 10] [--duration 30s] [--ramp-up 5s] [--think 0] " +
                                     "[--iterations n] [--request name] [--folder name] [--env env] [--max-error-rate 0.01] [--max-p95 500]");

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, bundled) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));
        var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);
        var requests = Program.Filter(collection, args);
        if (args.Flag("insecure"))
            requests = requests.Select(r =>
            {
                var copy = r.Clone();
                copy.Settings.VerifySsl = false;
                return copy;
            }).ToList();

        var options = new LoadOptions
        {
            Requests = requests,
            VirtualUsers = Math.Max(1, args.Int("users", 10)),
            Duration = args.Duration("duration", TimeSpan.FromSeconds(30)),
            RampUp = args.Duration("ramp-up", TimeSpan.Zero),
            ThinkTimeMs = args.Int("think", 0),
            IterationsPerUser = args.Int("iterations", 0),
            Environment = environment,
            CollectionVariables = collection.Variables,
            RunChecks = !args.Flag("no-checks")
        };

        Console.WriteLine($"Load testing {collection.Name}: {requests.Count} request(s), {options.VirtualUsers} users, " +
                          (options.IterationsPerUser > 0 ? $"{options.IterationsPerUser} iteration(s) each" : $"{options.Duration.TotalSeconds:0} s"));
        var progress = new Progress<LoadSnapshot>(s =>
            Console.WriteLine($"  {s.Elapsed.TotalSeconds,5:0}s  users {s.ActiveUsers,4}  requests {s.TotalRequests,7}  " +
                              $"{s.RequestsPerSecond,7:0.0} req/s  errors {s.Errors,5}  p95 {s.Latency.P95,6:0} ms"));

        var report = await services.GetRequiredService<LoadTester>().RunAsync(options, progress, cancellationToken);
        Console.WriteLine();
        Console.WriteLine(report.ToText());
        foreach (var error in report.SampleErrors.Take(5))
            Console.WriteLine($"  error: {error}");

        // Thresholds turn the load test into a CI gate.
        var failed = false;
        if (args.Option("max-error-rate") is { } maxErrors
            && double.TryParse(maxErrors, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxRate) && report.ErrorRate > maxRate)
        {
            Console.Error.WriteLine($"FAIL: error rate {report.ErrorRate:P2} exceeds {maxRate:P2}");
            failed = true;
        }
        if (args.Option("max-p95") is { } maxP95 && double.TryParse(maxP95, NumberStyles.Float, CultureInfo.InvariantCulture, out var p95)
                                                 && report.Latency.P95 > p95)
        {
            Console.Error.WriteLine($"FAIL: p95 {report.Latency.P95:0} ms exceeds {p95:0} ms");
            failed = true;
        }
        return failed ? Program.ExitTestsFailed : Program.ExitOk;
    }
}
