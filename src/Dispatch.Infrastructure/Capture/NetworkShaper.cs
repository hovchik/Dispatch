using System.Diagnostics;
using Dispatch.Application.Capture;

namespace Dispatch.Infrastructure.Capture;

/// <summary>Why the shaper refused a connection.</summary>
public enum NetworkDropReason
{
    /// <summary>The profile's packet-loss roll dropped this connection.</summary>
    Loss,

    /// <summary>The profile is in an offline window.</summary>
    Offline
}

/// <summary>Thrown inside the proxy when the shaper decides a connection is lost; the client sees it hang up.</summary>
public sealed class NetworkDroppedException(NetworkDropReason reason)
    : IOException(reason == NetworkDropReason.Offline ? "Dropped: the network profile is offline." : "Dropped: simulated packet loss.")
{
    public NetworkDropReason Reason { get; } = reason;
}

/// <summary>
/// Applies a <see cref="NetworkProfile"/> to proxied traffic: decides which connections to drop, waits out the added
/// latency, and paces response bytes to the bandwidth cap. One instance per proxy run; counters feed the UI / CLI.
/// </summary>
public sealed class NetworkShaper(NetworkProfile profile, Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private int _delayed, _dropped, _refusedOffline, _paced;

    public NetworkProfile Profile { get; } = profile;
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Connections that waited for the added latency.</summary>
    public int Delayed => _delayed;

    /// <summary>Connections dropped by the loss percentage.</summary>
    public int Dropped => _dropped;

    /// <summary>Connections refused because the link was offline.</summary>
    public int RefusedOffline => _refusedOffline;

    /// <summary>Responses that were slowed down to the bandwidth cap.</summary>
    public int Paced => _paced;

    public bool IsOffline => Profile.IsOfflineAt(_clock.Elapsed);

    /// <summary>Throws <see cref="NetworkDroppedException"/> when this connection does not make it through.</summary>
    public void Admit()
    {
        if (IsOffline)
        {
            Interlocked.Increment(ref _refusedOffline);
            throw new NetworkDroppedException(NetworkDropReason.Offline);
        }
        double roll;
        lock (_lock)
            roll = _random.NextDouble();
        if (Profile.ShouldDrop(roll))
        {
            Interlocked.Increment(ref _dropped);
            throw new NetworkDroppedException(NetworkDropReason.Loss);
        }
    }

    /// <summary>Waits out the profile's added latency.</summary>
    public async Task DelayAsync(CancellationToken ct)
    {
        if (Profile.Latency <= TimeSpan.Zero)
            return;
        Interlocked.Increment(ref _delayed);
        await Task.Delay(Profile.Latency, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="body"/> to <paramref name="stream"/> no faster than the bandwidth cap, in slices of about
    /// 50 ms so a slow download looks like one to the client, not like a pause followed by a burst.
    /// </summary>
    public async Task WritePacedAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (Profile.BandwidthBitsPerSecond <= 0 || body.Length == 0)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            return;
        }
        Interlocked.Increment(ref _paced);
        var sliceBytes = Math.Max(256, (int)(Profile.BandwidthBitsPerSecond / 8 / 20)); // ~50 ms worth
        var started = Stopwatch.GetTimestamp();
        long sent = 0;
        while (sent < body.Length)
        {
            var take = (int)Math.Min(sliceBytes, body.Length - sent);
            // Wait until the cap allows these bytes *before* handing them over: a body that fits in one slice must
            // still take its transfer time to arrive (the client has Content-Length and stops reading once it has it).
            var due = Profile.TransferTime(sent + take) - Stopwatch.GetElapsedTime(started);
            if (due > TimeSpan.Zero)
                await Task.Delay(due, ct).ConfigureAwait(false);
            await stream.WriteAsync(body.Slice((int)sent, take), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            sent += take;
        }
    }

    public string Describe() => Profile.Describe();
}
