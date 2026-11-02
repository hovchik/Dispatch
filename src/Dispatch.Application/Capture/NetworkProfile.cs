using System.Globalization;

namespace Dispatch.Application.Capture;

/// <summary>
/// Network conditions the capture (and client-fuzzing) proxy imposes on the traffic it relays: added latency, a
/// bandwidth cap on responses, random dropped connections, and periodic offline windows. Pure data plus the arithmetic
/// of when to drop, how long a transfer takes and when the link is "offline"; the proxy applies it.
/// </summary>
public sealed record NetworkProfile(
    string Name,
    TimeSpan Latency,
    long BandwidthBitsPerSecond,
    double LossPercent,
    TimeSpan OfflineEvery,
    TimeSpan OfflineFor)
{
    public static readonly NetworkProfile None = new("none", TimeSpan.Zero, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>Ready-made profiles, by name (case-insensitive). Latency is the added round-trip delay.</summary>
    public static IReadOnlyList<NetworkProfile> Presets { get; } =
    [
        new("2g", TimeSpan.FromMilliseconds(800), 50_000, 2, TimeSpan.Zero, TimeSpan.Zero),
        new("3g", TimeSpan.FromMilliseconds(300), 1_500_000, 0.5, TimeSpan.Zero, TimeSpan.Zero),
        new("4g", TimeSpan.FromMilliseconds(80), 12_000_000, 0.1, TimeSpan.Zero, TimeSpan.Zero),
        new("dsl", TimeSpan.FromMilliseconds(30), 8_000_000, 0, TimeSpan.Zero, TimeSpan.Zero),
        new("satellite", TimeSpan.FromMilliseconds(600), 5_000_000, 1, TimeSpan.Zero, TimeSpan.Zero),
        new("flaky-wifi", TimeSpan.FromMilliseconds(60), 6_000_000, 5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3)),
        new("offline", TimeSpan.Zero, 0, 0, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10))
    ];

    public bool IsNone => Latency <= TimeSpan.Zero && BandwidthBitsPerSecond <= 0 && LossPercent <= 0 && !HasOfflinePeriods;
    public bool HasOfflinePeriods => OfflineEvery > TimeSpan.Zero && OfflineFor > TimeSpan.Zero;

    public static NetworkProfile? FindPreset(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Presets.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
                                          || p.Name.Replace("-", "").Equals(name.Trim().Replace("-", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a profile from a preset name and/or individual settings; settings override the preset's values.
    /// Throws <see cref="FormatException"/> for values it cannot read.
    /// </summary>
    public static NetworkProfile Build(string? preset, string? latency = null, string? bandwidth = null, string? loss = null,
        string? offlineEvery = null, string? offlineFor = null)
    {
        var profile = preset is null
            ? None with { Name = "custom" }
            : FindPreset(preset) ?? throw new FormatException($"Unknown network profile '{preset}'. Presets: {string.Join(", ", Presets.Select(p => p.Name))}.");
        if (latency is not null)
            profile = profile with { Latency = ParseDuration(latency, "latency") };
        if (bandwidth is not null)
            profile = profile with { BandwidthBitsPerSecond = ParseBandwidth(bandwidth) };
        if (loss is not null)
            profile = profile with { LossPercent = ParsePercent(loss) };
        if (offlineEvery is not null)
            profile = profile with { OfflineEvery = ParseDuration(offlineEvery, "offline-every") };
        if (offlineFor is not null)
            profile = profile with { OfflineFor = ParseDuration(offlineFor, "offline-for") };
        if (preset is null && (latency ?? bandwidth ?? loss ?? offlineEvery ?? offlineFor) is not null)
            profile = profile with { Name = "custom" };
        return profile;
    }

    /// <summary>"300ms", "1.5s", "2m" or plain milliseconds.</summary>
    public static TimeSpan ParseDuration(string text, string what = "duration")
    {
        var t = text.Trim().ToLowerInvariant();
        double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d
            : throw new FormatException($"'{text}' is not a {what} (e.g. 300ms, 1.5s, 2m).");
        return t.EndsWith("ms") ? TimeSpan.FromMilliseconds(Number(t[..^2]))
            : t.EndsWith('s') ? TimeSpan.FromSeconds(Number(t[..^1]))
            : t.EndsWith('m') ? TimeSpan.FromMinutes(Number(t[..^1]))
            : TimeSpan.FromMilliseconds(Number(t));
    }

    /// <summary>"1mbps", "512kbps", "1.5 Mbit/s", "56k", plain bits per second, or "0"/"unlimited".</summary>
    public static long ParseBandwidth(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(" ", "").Replace("bit/s", "bps").Replace("b/s", "bps");
        if (t is "0" or "unlimited" or "none" or "")
            return 0;
        var multiplier = 1d;
        if (t.EndsWith("bps"))
            t = t[..^3];
        else if (t.EndsWith("bit"))
            t = t[..^3];
        if (t.EndsWith('g')) { multiplier = 1_000_000_000; t = t[..^1]; }
        else if (t.EndsWith('m')) { multiplier = 1_000_000; t = t[..^1]; }
        else if (t.EndsWith('k')) { multiplier = 1_000; t = t[..^1]; }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new FormatException($"'{text}' is not a bandwidth (e.g. 1mbps, 512kbps, 56k).");
        return (long)Math.Round(value * multiplier);
    }

    /// <summary>"2%", "0.5" (percent) or "2".</summary>
    public static double ParsePercent(string text)
    {
        var t = text.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 100)
            throw new FormatException($"'{text}' is not a loss percentage between 0 and 100.");
        return value;
    }

    /// <summary>Whether the link is in an offline window at <paramref name="sinceStart"/> after the proxy started.</summary>
    public bool IsOfflineAt(TimeSpan sinceStart)
    {
        if (!HasOfflinePeriods)
            return false;
        var cycle = sinceStart.Ticks % OfflineEvery.Ticks;
        // Online first, then offline for the last part of every cycle ("offline 10 s every minute").
        return cycle >= Math.Max(0, OfflineEvery.Ticks - OfflineFor.Ticks);
    }

    /// <summary>How long the next online stretch lasts from <paramref name="sinceStart"/> (zero when offline or no periods).</summary>
    public TimeSpan OnlineRemaining(TimeSpan sinceStart)
    {
        if (!HasOfflinePeriods || IsOfflineAt(sinceStart))
            return TimeSpan.Zero;
        var cycle = sinceStart.Ticks % OfflineEvery.Ticks;
        return TimeSpan.FromTicks(OfflineEvery.Ticks - OfflineFor.Ticks - cycle);
    }

    /// <summary>Whether this connection is dropped, given a roll in [0, 1).</summary>
    public bool ShouldDrop(double roll) => LossPercent > 0 && roll * 100 < LossPercent;

    /// <summary>How long <paramref name="bytes"/> take at the bandwidth cap (zero when uncapped).</summary>
    public TimeSpan TransferTime(long bytes) =>
        BandwidthBitsPerSecond <= 0 || bytes <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(bytes * 8d / BandwidthBitsPerSecond);

    public static string FormatBandwidth(long bitsPerSecond) => bitsPerSecond switch
    {
        <= 0 => "unlimited",
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000d:0.##} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000d:0.##} Mbps",
        >= 1_000 => $"{bitsPerSecond / 1_000d:0.##} kbps",
        _ => $"{bitsPerSecond} bps"
    };

    /// <summary>A one-line human description ("3g: +300 ms, 1.5 Mbps, 0.5% loss").</summary>
    public string Describe()
    {
        if (IsNone)
            return "no shaping";
        var parts = new List<string>();
        if (Latency > TimeSpan.Zero)
            parts.Add($"+{Latency.TotalMilliseconds:0} ms");
        if (BandwidthBitsPerSecond > 0)
            parts.Add(FormatBandwidth(BandwidthBitsPerSecond));
        if (LossPercent > 0)
            parts.Add($"{LossPercent.ToString("0.##", CultureInfo.InvariantCulture)}% loss");
        if (HasOfflinePeriods)
            parts.Add($"offline {OfflineFor.TotalSeconds:0.#} s every {OfflineEvery.TotalSeconds:0.#} s");
        return $"{Name}: {string.Join(", ", parts)}";
    }
}
