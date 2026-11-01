using System.Security.Cryptography;
using System.Text;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class StateAndVaultTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "toolkit-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData("ab", "use2")]
    [InlineData("tunnel-demo", "xy")]
    [InlineData("tunnel-demo", "not-a-cluster")]
    public void TunnelValidationRejectsIdsOutsideTheSdkContract(string tunnelId, string clusterId)
    {
        var profile = new TunnelProfile { AccountId = Guid.NewGuid().ToString("N"), TunnelId = tunnelId, ClusterId = clusterId };
        Assert.Equal("INVALID_TUNNEL_ID", Assert.Throws<ToolkitException>(() => Validation.Tunnel(profile)).Code);
    }

    [Fact]
    public void TunnelIdMaximumMatchesTheSdkDnsLimit()
    {
        var profile = new TunnelProfile { AccountId = Guid.NewGuid().ToString("N"), TunnelId = new string('a', 49), ClusterId = "euw" };
        Validation.Tunnel(profile);
        Assert.Equal("INVALID_TUNNEL_ID", Assert.Throws<ToolkitException>(() => Validation.Tunnel(profile with { TunnelId = new string('a', 50) })).Code);
    }

    [Fact]
    public async Task StateSurvivesRestartAndContainsNoSecrets()
    {
        var paths = new LocalPaths(directory);
        var account = new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser, DisplayName = "Test account" };
        var tunnel = new TunnelProfile { AccountId = account.Id, TunnelId = "sample-tunnel", ClusterId = "usw2", Mappings = [new() { RemotePort = 3140, LocalPort = 13140, Mode = MappingMode.Manual }] };
        var expected = new ToolkitState([account], [tunnel], new() { Theme = "Dark" });
        await using (var store = new SqliteStateStore(paths))
        {
            await store.SaveAsync(expected);
            await store.AppendLogAsync(new(1, DateTimeOffset.UtcNow, "Information", "TEST", "Test event"));
        }
        await using var reopened = new SqliteStateStore(paths);
        var loaded = await reopened.LoadAsync();
        Assert.Equal(account, Assert.Single(loaded.Accounts));
        Assert.Equal(tunnel.CanonicalId, Assert.Single(loaded.Tunnels).CanonicalId);
        Assert.Equal(13140, loaded.Tunnels[0].Mappings[0].LocalPort);
        Assert.Equal("Dark", loaded.Settings.Theme);
        Assert.Single(await reopened.ReadLogsAsync(100));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.DatabasePath));
    }

    [Fact]
    public async Task VaultEncryptsAndBindsEachSecretToItsReferenceAndKey()
    {
        var paths = new LocalPaths(directory);
        paths.Initialize();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var vault = new CredentialVault(paths, key);
        await vault.WriteAsync("account.first.token", "sensitive-test-value");
        Assert.Equal("sensitive-test-value", await vault.ReadAsync("account.first.token"));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.VaultDirectory, "account.first.token.secret"));
        Assert.DoesNotContain("sensitive-test-value", Encoding.UTF8.GetString(bytes));
        File.Copy(Path.Combine(paths.VaultDirectory, "account.first.token.secret"), Path.Combine(paths.VaultDirectory, "account.other.token.secret"));
        Assert.Equal("VAULT_DECRYPTION_FAILED", (await Assert.ThrowsAsync<ToolkitException>(() => vault.ReadAsync("account.other.token"))).Code);
        using var wrongKey = new CredentialVault(paths, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.Equal("VAULT_DECRYPTION_FAILED", (await Assert.ThrowsAsync<ToolkitException>(() => wrongKey.ReadAsync("account.first.token"))).Code);
        await vault.DeleteAsync("account.first.token");
        Assert.Null(await vault.ReadAsync("account.first.token"));
    }

    [Fact]
    public async Task VaultRejectsTraversalAndSymlinks()
    {
        var paths = new LocalPaths(directory);
        paths.Initialize();
        using var vault = new CredentialVault(paths, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<ToolkitException>(() => vault.WriteAsync("../escape", "test"));
        if (!OperatingSystem.IsWindows())
        {
            var target = Path.Combine(directory, "target");
            await File.WriteAllTextAsync(target, "original");
            File.CreateSymbolicLink(Path.Combine(paths.VaultDirectory, "linked.secret"), target);
            await Assert.ThrowsAsync<ToolkitException>(() => vault.WriteAsync("linked", "changed"));
            Assert.Equal("original", await File.ReadAllTextAsync(target));
        }
    }

    [Fact]
    public async Task DisposedVaultRejectsEveryOperationWithoutRecreatingSecretFiles()
    {
        var paths = new LocalPaths(directory);
        using var vault = new CredentialVault(paths, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await vault.WriteAsync("account.test.token", "saved-value");
        var before = await File.ReadAllBytesAsync(Path.Combine(paths.VaultDirectory, "account.test.token.secret"));
        vault.Dispose();
        vault.Dispose();
        Assert.False(vault.Available);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vault.ReadAsync("account.test.token"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vault.WriteAsync("account.test.token", "replacement"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vault.DeleteAsync("account.test.token"));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(paths.VaultDirectory, "account.test.token.secret")));
    }

    [Fact]
    public async Task VaultBoundsSecretsByUtf8BytesBeforeEncodingOrWriting()
    {
        var paths = new LocalPaths(directory);
        using var vault = new CredentialVault(paths, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var oversized = new string('\u03bb', CredentialVault.MaxSecretBytes / 2 + 1);
        Assert.True(oversized.Length < CredentialVault.MaxSecretBytes);
        Assert.Equal("SECRET_TOO_LARGE", (await Assert.ThrowsAsync<ToolkitException>(() => vault.WriteAsync("account.test.token", oversized))).Code);
        Assert.Empty(Directory.GetFiles(paths.VaultDirectory));
        var boundary = new string('\u03bb', CredentialVault.MaxSecretBytes / 2);
        await vault.WriteAsync("account.test.token", boundary);
        Assert.Equal(boundary, await vault.ReadAsync("account.test.token"));
    }

    [Fact]
    public async Task VaultRejectsOversizedEncryptedFilesBeforeDecrypting()
    {
        var paths = new LocalPaths(directory);
        using var vault = new CredentialVault(paths, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var path = Path.Combine(paths.VaultDirectory, "account.test.token.secret");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(CredentialVault.MaxSecretBytes + 4097);
        Assert.Equal("SECRET_TOO_LARGE", (await Assert.ThrowsAsync<ToolkitException>(() => vault.ReadAsync("account.test.token"))).Code);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.1")]
    [InlineData("example.invalid")]
    public void MappingDefaultsNeverExposeTheNetwork(string address)
    {
        Assert.Equal("LOOPBACK_REQUIRED", Assert.Throws<ToolkitException>(() => Validation.Mapping(new() { RemotePort = 3140, LocalAddress = address })).Code);
    }

    [Fact]
    public async Task LogRetentionDeletesOldEntriesAndCapsReads()
    {
        await using var store = new SqliteStateStore(new(directory));
        await store.AppendLogAsync(new(1, DateTimeOffset.UtcNow.AddDays(-8), "Information", "OLD", "Old"));
        await store.AppendLogAsync(new(2, DateTimeOffset.UtcNow, "Information", "NEW", "New"));
        await store.PruneLogsAsync(7);
        Assert.Equal("NEW", Assert.Single(await store.ReadLogsAsync(200)).Code);
    }
}
