using Dispatch.Application.Webhooks;
using Dispatch.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class ShutdownAndAboutTests
{
    [Fact]
    public async Task Service_container_with_async_only_singletons_disposes_asynchronously()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dispatch-shutdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection().AddDispatchCore(Path.Combine(dir, "dispatch.db")).BuildServiceProvider();
            // Resolving the receiver is what made the app's synchronous Dispose() throw on close (crash log of 2026-11-18).
            Assert.NotNull(services.GetRequiredService<IWebhookReceiver>());
            Assert.Throws<InvalidOperationException>(() => services.Dispose());

            var again = new ServiceCollection().AddDispatchCore(Path.Combine(dir, "dispatch.db")).BuildServiceProvider();
            again.GetRequiredService<IWebhookReceiver>();
            await again.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void About_info_reads_version_build_date_and_copyright_from_the_assembly()
    {
        var info = AppInfo.For(typeof(AppInfo).Assembly);

        Assert.Equal("Dispatch", info.Product);
        Assert.Matches(@"^\d+\.\d+\.\d+", info.Version);
        Assert.DoesNotContain("+", info.Version);
        Assert.NotNull(info.BuildDate);
        Assert.Contains(AppInfo.Owner, info.Copyright);
        Assert.Contains(info.BuildDate!.Value.Year.ToString(), info.Copyright);
        Assert.StartsWith("© 2026", info.Copyright);
        Assert.False(string.IsNullOrWhiteSpace(info.Description));
    }
}
