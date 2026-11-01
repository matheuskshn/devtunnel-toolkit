using System.Buffers.Binary;
using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Ipc;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class SecurityBoundaryTests
{
    [Fact]
    public async Task LargestSupportedWorkspaceFitsABoundedReplyFrame()
    {
        var accounts = Enumerable.Range(0, 50).Select(_ => new AccountProfile
        {
            Provider = AccountProvider.GitHub,
            DisplayName = new string('\u03bb', 120),
            ClientId = new string('c', 128),
            Scope = new string('s', 500)
        }).ToArray();
        var profiles = Enumerable.Range(0, 200).Select(index => new TunnelProfile
        {
            AccountId = accounts[index % accounts.Length].Id,
            TunnelId = $"sample-tunnel-{index:D3}",
            ClusterId = "usw2",
            Alias = new string('\u03bb', 120),
            Mappings = Enumerable.Range(1, 100).Select(port => new PortMapping
            {
                RemotePort = port,
                LocalAddress = "0:0:0:0:0:0:0:1",
                LocalPort = port + 10000,
                LastEffectivePort = port + 10000,
                Mode = MappingMode.Automatic,
                RangeStart = 10000,
                RangeEnd = 20000
            }).ToArray()
        }).ToArray();
        Assert.All(accounts, Validation.Account);
        Assert.All(profiles, Validation.Tunnel);
        var connections = profiles.Select(profile => new ConnectionStatus(profile.Id, ConnectionState.Connected,
            Ports: profile.Mappings.Select(mapping => new EffectiveMapping(mapping.RemotePort, mapping.LocalAddress, mapping.LocalPort)).ToArray())).ToArray();
        var snapshot = new AgentSnapshot(accounts, profiles, connections, [], new(), new("Test", true, true, true, true), []);
        var reply = new AgentReply(true, JsonSerializer.SerializeToElement(snapshot, WireProtocol.Json));
        using var stream = new MemoryStream();
        await WireProtocol.WriteAsync(stream, reply, default);
        Assert.True(stream.Length > WireProtocol.MaxFrameBytes);
        Assert.True(stream.Length < WireProtocol.MaxReplyFrameBytes + 4);
        stream.Position = 0;
        var decoded = await WireProtocol.ReadAsync<AgentReply>(stream, default);
        var decodedState = decoded.Result!.Value.Deserialize<AgentSnapshot>(WireProtocol.Json)!;
        Assert.Equal(200, decodedState.Tunnels.Length);
        Assert.All(decodedState.Tunnels, profile => Assert.Equal(100, profile.Mappings.Length));
    }

    [Fact]
    public async Task RequestAndReplyLengthsAreRejectedBeforeReadingTheirBodies()
    {
        using var request = HeaderOnly(WireProtocol.MaxFrameBytes + 1);
        Assert.Equal("IPC_INVALID_FRAME", (await Assert.ThrowsAsync<ToolkitException>(() => WireProtocol.ReadAsync<AgentRequest>(request, default))).Code);
        using var reply = HeaderOnly(WireProtocol.MaxReplyFrameBytes + 1);
        Assert.Equal("IPC_INVALID_FRAME", (await Assert.ThrowsAsync<ToolkitException>(() => WireProtocol.ReadAsync<AgentReply>(reply, default))).Code);
    }

    [Fact]
    public async Task LargeReplyBudgetDoesNotIncreaseTheAcceptedRequestBudget()
    {
        using var output = new MemoryStream();
        var request = new AgentRequest(WireProtocol.Version, "ignored", JsonSerializer.SerializeToElement(new { value = new string('x', WireProtocol.MaxFrameBytes) }));
        Assert.Equal("IPC_FRAME_TOO_LARGE", (await Assert.ThrowsAsync<ToolkitException>(() => WireProtocol.WriteAsync(output, request, default))).Code);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task IpcShutdownWaitsForConcurrentClientCleanupRepeatedly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "toolkit-ipc-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var paths = new LocalPaths(directory);
                using var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var count = 0;
                var server = new ToolkitServer(paths, async (_, _, cancellation) =>
                {
                    if (Interlocked.Increment(ref count) == 8) entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                    return new object();
                }, () => throw new InvalidOperationException("Watch is not part of this test"));
                var run = server.RunAsync(stopped.Token);
                var client = new ToolkitClient(directory);
                var calls = Enumerable.Range(0, 8).Select(async _ =>
                {
                    try { await client.CallAsync<object>("pending", cancellationToken: stopped.Token); }
                    catch (Exception error) when (error is OperationCanceledException or IOException or ToolkitException) { }
                }).ToArray();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await stopped.CancelAsync();
                    await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(3));
                    await run.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.True(run.IsCompletedSuccessfully);
                }
                finally
                {
                    await stopped.CancelAsync();
                    await run.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static MemoryStream HeaderOnly(int size)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        return new MemoryStream(header);
    }
}
