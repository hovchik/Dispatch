using System.Collections.Concurrent;
using Dispatch.Application.Capture;
using Dispatch.Application.Interop;
using Dispatch.Domain;
using Dispatch.Infrastructure.Capture;

namespace Dispatch.Cli;

/// <summary>Runs the capture proxy from the terminal and writes what it records to a HAR or Dispatch collection.</summary>
public static class CaptureCommand
{
    private static readonly HashSet<string> Flags = ["public", "no-decrypt", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["p"] = "port", ["o"] = "out" });

        using var authority = new CertificateAuthority(CertificateAuthority.LoadOrCreate(Dispatch.Infrastructure.DispatchPaths.CaptureCaFile));
        if (args.Option("export-ca") is { } caPath)
        {
            await File.WriteAllTextAsync(caPath, authority.CaCertificatePem, cancellationToken);
            Console.WriteLine($"CA certificate written to {Path.GetFullPath(caPath)} — trust it to decrypt HTTPS.");
        }

        var captured = new ConcurrentQueue<CapturedExchange>();
        await using var proxy = new CaptureProxy(authority);
        var color = !args.Flag("no-color") && !Console.IsOutputRedirected;
        proxy.Captured += e =>
        {
            captured.Enqueue(e);
            var mark = e.IsSuccess ? Paint("✓", "32", color) : Paint("✗", "31", color);
            Console.WriteLine($"{mark} {e.Method,-6} {e.StatusCode} {e.Url}  ({e.ElapsedMs:0} ms)");
        };

        await proxy.StartAsync(new CaptureProxyOptions
        {
            Port = args.Int("port", 8899),
            Public = args.Flag("public"),
            HostFilter = args.Option("host") ?? "",
            DecryptHttps = !args.Flag("no-decrypt")
        });

        Console.WriteLine($"Capture proxy listening on http://127.0.0.1:{proxy.Port}");
        Console.WriteLine($"Point HTTP(S)_PROXY or your client at it. For HTTPS, trust the CA (--export-ca <file>).");
        Console.WriteLine("Press Ctrl+C to stop and save.");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        await proxy.StopAsync();

        var exchanges = captured.ToList();
        Console.WriteLine($"\nCaptured {exchanges.Count} exchange(s).");
        if (exchanges.Count == 0 || args.Option("out") is not { } outPath)
            return Program.ExitOk;

        if (outPath.EndsWith(".har", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllTextAsync(outPath, CaptureConverter.ToHar(exchanges), CancellationToken.None);
        }
        else
        {
            var collection = new RequestCollection { Name = args.Option("name") ?? "Captured" };
            foreach (var exchange in exchanges)
            {
                var request = CaptureConverter.ToRequest(exchange);
                request.CollectionId = collection.Id;
                collection.Requests.Add(request);
            }
            await File.WriteAllTextAsync(outPath, DispatchFormat.ExportCollection(collection), CancellationToken.None);
        }
        Console.WriteLine($"Saved to {Path.GetFullPath(outPath)}");
        return Program.ExitOk;
    }

    private static string Paint(string text, string code, bool color) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
