using System.Globalization;
using System.Reflection;

namespace Dispatch.Infrastructure;

/// <summary>What the About card shows: version, build date and copyright, read from the app's assembly attributes.</summary>
public sealed record AppInfo(string Product, string Version, DateOnly? BuildDate, string Copyright, string Description)
{
    /// <summary>The year the project started; the copyright runs from here to the build year.</summary>
    public const int FirstYear = 2026;

    public const string Owner = "Hovhannes";

    public const string DefaultDescription =
        "A fast, cross-platform API client for developers and QA engineers. Send requests over HTTP, GraphQL, gRPC, SOAP, " +
        "WebSocket, SSE, Socket.IO, MQTT, Kafka, AMQP and raw TCP/UDP, then test the responses with assertions, " +
        "a collection runner, mocks, contract checks and load tests.";

    public string Runtime => $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    public string BuildDateText => BuildDate?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) ?? "";

    public static AppInfo For(Assembly assembly)
    {
        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product is { Length: > 0 } p ? p : "Dispatch";

        // InformationalVersion carries "+<commit>" from SourceLink; the About card shows just the version number.
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } info
            ? info.Split('+')[0]
            : assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        DateOnly? buildDate = null;
        var stamp = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value;
        if (DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            buildDate = parsed;

        var year = Math.Max(FirstYear, buildDate?.Year ?? DateTime.Now.Year);
        var years = year > FirstYear ? $"{FirstYear}–{year}" : $"{FirstYear}";
        var copyright = $"© {years} {Owner}. All rights reserved.";

        return new AppInfo(product, version, buildDate, copyright, DefaultDescription);
    }
}
