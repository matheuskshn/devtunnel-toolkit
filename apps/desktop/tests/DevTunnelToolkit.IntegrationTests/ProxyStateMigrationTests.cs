using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class ProxyStateMigrationTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    public async Task LegacySettingsLoadSafelyAndSaveUpgradesWithoutLosingProfiles(string schema)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dtk-migration-" + Guid.NewGuid().ToString("N"));
        var paths = new LocalPaths(directory);
        try
        {
            var account = new AccountProfile { Provider = AccountProvider.GitHub, DisplayName = "Migration fixture" };
            await using (var initialized = new SqliteStateStore(paths)) { }
            await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString()))
            {
                await raw.OpenAsync(); using var command = raw.CreateCommand();
                command.CommandText = "UPDATE metadata SET value=$schema WHERE key='schema'; INSERT INTO metadata(key,value) VALUES('state',$state);";
                command.Parameters.AddWithValue("$schema", schema);
                command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(new { accounts = new[] { account }, tunnels = Array.Empty<TunnelProfile>(), settings = new { theme = "Dark" } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                await command.ExecuteNonQueryAsync();
            }
            var script = "function FindProxyForURL(url, host) { return 'DIRECT'; }";
            await using (var store = new SqliteStateStore(paths))
            {
                var legacy = await store.LoadAsync();
                Assert.False(legacy.Settings.Proxy.Enabled); Assert.True(legacy.Settings.CloseToTray);
                Assert.Equal(PacMode.Rules, legacy.Settings.Pac.Mode); Assert.Equal(account, Assert.Single(legacy.Accounts));
                await store.SaveAsync(legacy with { Settings = legacy.Settings with { Pac = new() { Mode = PacMode.Manual, ManualScript = script }, Proxy = new() { Rules = [new() { Destination = "example.org", Action = RouteAction.Block }] } } });
            }
            await using (var reopened = new SqliteStateStore(paths))
            {
                var saved = await reopened.LoadAsync(); Assert.Equal(account, Assert.Single(saved.Accounts));
                Assert.Equal("Dark", saved.Settings.Theme); Assert.Equal(script, saved.Settings.Pac.ManualScript);
                Assert.Equal(RouteAction.Block, Assert.Single(saved.Settings.Proxy.Rules).Action);
            }
            await using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
            await check.OpenAsync(); using var version = check.CreateCommand(); version.CommandText = "SELECT value FROM metadata WHERE key='schema'";
            Assert.Equal("5", await version.ExecuteScalarAsync());
        }
        finally { Directory.Delete(directory, true); }
    }
}
