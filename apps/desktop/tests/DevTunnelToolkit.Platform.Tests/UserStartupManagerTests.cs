using DevTunnelToolkit.Core;
using Xunit;
using static DevTunnelToolkit.Platform.UserStartupManager;

namespace DevTunnelToolkit.Platform.Tests;

public sealed class UserStartupManagerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devtunnel-startup-tests-" + Guid.NewGuid().ToString("N"));

    public UserStartupManagerTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(StartupKind.Linux)]
    [InlineData(StartupKind.Windows)]
    internal async Task RegistrationIsOwnedPerDataDirectoryAndRemovable(StartupKind kind)
    {
        var backend = new FakeBackend(kind);
        var manager = Create(backend);
        Assert.True(manager.Supported);
        Assert.False(await manager.IsEnabledAsync());

        await manager.SetEnabledAsync(true);

        Assert.True(await manager.IsEnabledAsync());
        var entry = Assert.Single(backend.Entries);
        Assert.Contains("--data-dir", entry.Value.Content);
        Assert.Contains("DevTunnelToolkit.Agent", entry.Value.Content);
        Assert.DoesNotContain("DevTunnelToolkit.App", entry.Value.Content);
        Assert.DoesNotContain("--now", entry.Value.Content);
        await manager.SetEnabledAsync(false);
        Assert.False(await manager.IsEnabledAsync());
        Assert.Empty(backend.Entries);
    }

    [Fact]
    public async Task ProfilesUseDifferentRegistrationNames()
    {
        var backend = new FakeBackend(StartupKind.Linux);
        await Create(backend, "profile-one").SetEnabledAsync(true);
        await Create(backend, "profile-two").SetEnabledAsync(true);
        Assert.Equal(2, backend.Entries.Count);
    }

    [Fact]
    public async Task UnownedRegistrationIsNeverOverwrittenOrRemoved()
    {
        var backend = new FakeBackend(StartupKind.Linux);
        var manager = Create(backend);
        await manager.SetEnabledAsync(true);
        var name = Assert.Single(backend.Entries).Key;
        backend.Entries[name] = new StartupEntry("unrelated startup command", false);

        var enable = await Assert.ThrowsAsync<ToolkitException>(() => manager.SetEnabledAsync(true));
        var disable = await Assert.ThrowsAsync<ToolkitException>(() => manager.SetEnabledAsync(false));

        Assert.Equal("STARTUP_ENTRY_CONFLICT", enable.Code);
        Assert.Equal("STARTUP_ENTRY_CONFLICT", disable.Code);
        Assert.Equal("unrelated startup command", backend.Entries[name].Content);
    }

    [Fact]
    public async Task FailedActivationRollsBackNewRegistration()
    {
        var backend = new FakeBackend(StartupKind.Linux) { FailNextActivation = true };
        var manager = Create(backend);

        await Assert.ThrowsAsync<IOException>(() => manager.SetEnabledAsync(true));

        Assert.Empty(backend.Entries);
        Assert.False(await manager.IsEnabledAsync());
    }

    [Fact]
    public async Task FailedUpdateRestoresPreviousContentAndState()
    {
        var backend = new FakeBackend(StartupKind.Linux);
        var manager = Create(backend);
        await manager.SetEnabledAsync(true);
        var name = Assert.Single(backend.Entries).Key;
        var previous = new StartupEntry("owned previous installation", true);
        backend.Entries[name] = previous;
        backend.FailNextActivation = true;

        await Assert.ThrowsAsync<IOException>(() => manager.SetEnabledAsync(true));

        Assert.Equal(previous, backend.Entries[name]);
        Assert.True(await manager.IsEnabledAsync());
    }

    [Fact]
    public async Task UnsupportedBackendAndDotnetDevelopmentLauncherAreExplicit()
    {
        var unsupported = Create(new FakeBackend(StartupKind.Linux) { Available = false });
        Assert.False(unsupported.Supported);
        var error = await Assert.ThrowsAsync<ToolkitException>(() => unsupported.SetEnabledAsync(true));
        Assert.Equal("USER_STARTUP_UNAVAILABLE", error.Code);
        var dotnet = Path.Combine(directory, "dotnet");
        await File.WriteAllTextAsync(dotnet, "development executable");
        var development = new UserStartupManager(dotnet, directory, new FakeBackend(StartupKind.Linux));
        Assert.False(development.Supported);
    }

    [Fact]
    public async Task RepeatedEnableDoesNotRewriteExistingRegistration()
    {
        var backend = new FakeBackend(StartupKind.Linux);
        var manager = Create(backend);
        await manager.SetEnabledAsync(true);
        await manager.SetEnabledAsync(true);
        Assert.Equal(1, backend.Writes);
    }

    [Fact]
    public void SystemdArgumentsEscapeSpecifiersAndVariableExpansion()
    {
        Assert.Equal("\"/tmp/path %%i $$name \\\"quoted\\\"\\\\end\"", QuoteSystemd("/tmp/path %i $name \"quoted\"\\end"));
    }

    [Theory]
    [InlineData("simple", "\"simple\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("ends\\", "\"ends\\\\\"")]
    [InlineData("say\"hello", "\"say\\\"hello\"")]
    [InlineData("slash\\\"quote", "\"slash\\\\\\\"quote\"")]
    public void WindowsArgumentsFollowCommandLineQuoting(string value, string expected) =>
        Assert.Equal(expected, QuoteWindows(value));

    private UserStartupManager Create(FakeBackend backend, string dataName = "data")
    {
        var executable = Path.Combine(directory, backend.Kind == StartupKind.Windows ? "DevTunnelToolkit.Agent.exe" : "DevTunnelToolkit.Agent");
        File.WriteAllText(executable, "test executable");
        return new UserStartupManager(executable, Path.Combine(directory, dataName), backend);
    }

    public void Dispose() => Directory.Delete(directory, true);

    private sealed class FakeBackend(StartupKind kind) : IStartupBackend
    {
        public StartupKind Kind => kind;
        public bool Available { get; init; } = true;
        public Dictionary<string, StartupEntry> Entries { get; } = [];
        public HashSet<string> Enabled { get; } = [];
        public bool FailNextActivation { get; set; }
        public int Writes { get; private set; }
        public Task<StartupEntry?> ReadAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Entries.GetValueOrDefault(name));
        public Task WriteAsync(string name, string content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            Entries[name] = new StartupEntry(content, true);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            Entries.Remove(name);
            return Task.CompletedTask;
        }
        public Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
        {
            if (FailNextActivation) { FailNextActivation = false; throw new IOException("Activation failed."); }
            if (enabled) Enabled.Add(name); else Enabled.Remove(name);
            return Task.CompletedTask;
        }
        public Task<bool> IsEnabledAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Enabled.Contains(name));
    }
}
