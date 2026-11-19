using System.Net;
using System.Net.Sockets;
using Dispatch.Infrastructure.Capture;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Tests;

/// <summary>Edge cases around ports, concurrent writes and key files that used to lock up a tool or lose data.</summary>
public sealed class RobustnessTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dispatch-robust-{Guid.NewGuid():N}");
    private DbFactory _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new DbFactory(Path.Combine(_root, "test.db"));
        await new DatabaseInitializer(_factory).InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Capture_proxy_on_a_busy_port_stays_stopped_and_can_start_on_another()
    {
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);

        await Assert.ThrowsAsync<SocketException>(() => proxy.StartAsync(new CaptureProxyOptions { Port = port }));

        Assert.False(proxy.IsRunning);
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0 });
        Assert.True(proxy.IsRunning);
        Assert.NotEqual(port, proxy.Port);
    }

    [Fact]
    public async Task Concurrent_writes_of_a_new_setting_all_succeed()
    {
        var settings = new SettingsRepository(_factory);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => settings.SetAsync("race", i.ToString()))));

        Assert.NotNull(await settings.GetAsync("race"));
        await settings.SetAsync("race", "final");
        Assert.Equal("final", await settings.GetAsync("race"));
        await settings.SetAsync("race", null);
        Assert.Null(await settings.GetAsync("race"));
    }

    [Fact]
    public async Task Protectors_created_together_share_one_key()
    {
        var dir = Path.Combine(_root, "keys");
        var protectors = Enumerable.Range(0, 8).Select(_ => new SecretProtector(dir)).ToList();

        var cipherTexts = await Task.WhenAll(protectors.Select(p => Task.Run(() => p.Protect("s3cret"))));

        foreach (var protector in protectors)
            foreach (var cipher in cipherTexts)
                Assert.Equal("s3cret", protector.Unprotect(cipher));
        Assert.False(File.Exists(Path.Combine(dir, SecretProtector.FileKeyName + ".tmp")));
    }

    private sealed class DbFactory(string path) : IDbContextFactory<DispatchDbContext>
    {
        public DispatchDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DispatchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
