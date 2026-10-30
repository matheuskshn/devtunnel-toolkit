using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class ServiceMigrationTests
{
    [Fact]
    public async Task LegacyDatabaseMigratesEachProxyIndependentlyAndPersistsStableReferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dtk-service-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalPaths(directory);
            var account = new AccountProfile { Provider = AccountProvider.GitHub, DisplayName = "Migration fixture" };
            var host = new HostProfile
            {
                AccountId = account.Id,
                TunnelId = "migration-fixture",
                ClusterId = "use2",
                Alias = "Original",
                AutoStart = true,
                Ports = [new() { RemotePort = 3140, LocalPort = 23140 }, new() { RemotePort = 3180, Kind = HostPortKind.Socks5Proxy, LocalPort = 0 }, new() { RemotePort = 8080, Kind = HostPortKind.Service, LocalPort = 18080 }],
                Proxy = new() { ConnectTimeoutSeconds = 21, MaxClients = 51, Rules = [new() { Destination = "example.org", Ports = [443] }] }
            };
            var original = new ToolkitState([account], [], new()) { Hosts = [host, host with { Id = Guid.NewGuid().ToString("N"), TunnelId = "another-fixture", AutoStart = false }] };
            await using (var seed = new SqliteStateStore(paths)) await seed.SaveAsync(original);
            await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString()))
            {
                await raw.OpenAsync(); using var command = raw.CreateCommand(); command.CommandText = "UPDATE metadata SET value='4' WHERE key='schema'"; await command.ExecuteNonQueryAsync();
            }
            ToolkitState migrated;
            await using (var store = new SqliteStateStore(paths)) migrated = await store.LoadAsync();
            Assert.Equal(4, migrated.Services.Length);
            Assert.Equal(4, migrated.Services.Select(s => s.Id).Distinct().Count());
            Assert.Equal(2, migrated.Services.Count(s => s.AutoStart));
            Assert.Equal(23140, migrated.Services.Single(s => s.Id == migrated.Hosts[0].Ports[0].ServiceId).Port);
            Assert.Equal(0, migrated.Services.Single(s => s.Id == migrated.Hosts[0].Ports[1].ServiceId).Port);
            Assert.All(migrated.Services, s => { Assert.Equal(21, s.Policy.ConnectTimeoutSeconds); Assert.Equal(51, s.Policy.MaxClients); Assert.Equal("example.org", Assert.Single(s.Policy.Rules).Destination); });
            Assert.Null(migrated.Hosts[0].Ports[2].ServiceId);
            Assert.Same(migrated, ServiceMigration.Upgrade(migrated));
            await using (var reopened = new SqliteStateStore(paths))
                Assert.Equal(JsonSerializer.Serialize(migrated), JsonSerializer.Serialize(await reopened.LoadAsync()));
            await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
            await verify.OpenAsync(); using var version = verify.CreateCommand(); version.CommandText = "SELECT value FROM metadata WHERE key='schema'";
            Assert.Equal("5", await version.ExecuteScalarAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MigrationNeverOverwritesCollidingIndependentService()
    {
        var account = new AccountProfile { Provider = AccountProvider.GitHub };
        var host = new HostProfile { AccountId = account.Id, TunnelId = "migration-fixture", ClusterId = "use2", Ports = [new()] };
        var original = new ToolkitState([account], [], new()) { Hosts = [host] };
        var migrated = ServiceMigration.Upgrade(original);
        var conflict = original with { Services = migrated.Services };
        Assert.Equal("SERVICE_MIGRATION_CONFLICT", Assert.Throws<ToolkitException>(() => ServiceMigration.Upgrade(conflict)).Code);
        Assert.Null(Assert.Single(original.Hosts).Ports[0].ServiceId);
        Assert.Empty(original.Services);
    }
}
