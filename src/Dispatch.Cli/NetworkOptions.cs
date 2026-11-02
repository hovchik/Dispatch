using Dispatch.Application.Capture;

namespace Dispatch.Cli;

/// <summary>
/// The network-condition options shared by <c>dispatch capture</c> and <c>dispatch fuzz-client</c>:
/// <c>--network 3g</c> picks a preset; <c>--latency</c>, <c>--bandwidth</c>, <c>--loss</c>, <c>--offline-every</c> and
/// <c>--offline-for</c> set or override individual values.
/// </summary>
internal static class NetworkOptions
{
    public const string Usage = "[--network 3g|4g|2g|dsl|satellite|flaky-wifi|offline] [--latency 300ms] [--bandwidth 1mbps] [--loss 2%] " +
                                "[--offline-every 60s --offline-for 10s]";

    /// <summary>The profile the arguments describe, or null when none of the options were given.</summary>
    public static NetworkProfile? Parse(Arguments args)
    {
        var preset = args.Option("network");
        var latency = args.Option("latency");
        var bandwidth = args.Option("bandwidth");
        var loss = args.Option("loss");
        var offlineEvery = args.Option("offline-every");
        var offlineFor = args.Option("offline-for");
        if ((preset ?? latency ?? bandwidth ?? loss ?? offlineEvery ?? offlineFor) is null)
            return null;
        try
        {
            var profile = NetworkProfile.Build(preset, latency, bandwidth, loss, offlineEvery, offlineFor);
            if ((offlineEvery is null) != (offlineFor is null) && !profile.HasOfflinePeriods)
                throw new UsageException("--offline-every and --offline-for go together (e.g. --offline-every 60s --offline-for 10s).");
            return profile;
        }
        catch (FormatException ex)
        {
            throw new UsageException(ex.Message);
        }
    }
}
