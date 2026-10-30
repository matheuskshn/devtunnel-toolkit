using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class PublishedProxyTests
{
    private static HostProfile Profile(params PublishedProxyRule[] rules) => new()
    {
        AccountId = new string('a', 32),
        TunnelId = "synthetic-host",
        ClusterId = "use2",
        Ports = [new() { RemotePort = 3140, Kind = HostPortKind.HttpProxy }, new() { RemotePort = 3180, Kind = HostPortKind.Socks5Proxy }],
        Proxy = new() { Rules = rules }
    };

    private static PublishedProxyRule Loopback(int port) => new()
    { Kind = DestinationKind.Ip, Destination = "127.0.0.1", AllowLoopback = true, Ports = [port] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedHttpAndSocksRelayToOnlyAllowedFixture(bool http)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var proxy = new PublishedProxyService(Profile(Loopback(originPort)), (_, _) => { });
        proxy.Start();
        Assert.All(proxy.Ports, p => { Assert.True(IPAddress.IsLoopback(IPAddress.Parse(p.LocalAddress))); Assert.True(p.LocalPort > 0); });
        Assert.Equal(2, proxy.Ports.Select(p => p.LocalPort).Distinct().Count());
        var target = proxy.Ports.Single(p => p.Kind == (http ? HostPortKind.HttpProxy : HostPortKind.Socks5Proxy));
        var fixture = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            var buffer = new byte[4]; await peer.GetStream().ReadExactlyAsync(buffer, timeout.Token);
            Assert.Equal("ping", Encoding.ASCII.GetString(buffer));
            await peer.GetStream().WriteAsync("pong"u8.ToArray(), timeout.Token);
        });
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, target.LocalPort, timeout.Token);
        if (http)
        {
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{originPort} HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"), timeout.Token);
            Assert.StartsWith("HTTP/1.1 200", await Header(client.GetStream(), timeout.Token));
        }
        else Assert.Equal(0, await Socks(client.GetStream(), "127.0.0.1", originPort, timeout.Token));
        await client.GetStream().WriteAsync("ping"u8.ToArray(), timeout.Token);
        var response = new byte[4]; await client.GetStream().ReadExactlyAsync(response, timeout.Token);
        Assert.Equal("pong", Encoding.ASCII.GetString(response)); await fixture;
    }

    [Fact]
    public async Task PublishedHttpPreservesHostAndOriginFormWithoutMitm()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var proxy = new PublishedProxyService(Profile(Loopback(port)), (_, _) => { }); proxy.Start();
        var served = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            var header = await Header(peer.GetStream(), timeout.Token);
            Assert.StartsWith("GET /fixture HTTP/1.1", header);
            Assert.Contains($"Host: 127.0.0.1:{port}", header);
            await peer.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), timeout.Token);
        });
        using var client = new HttpClient(new HttpClientHandler
        { Proxy = new WebProxy($"http://127.0.0.1:{proxy.Ports.Single(p => p.Kind == HostPortKind.HttpProxy).LocalPort}"), UseProxy = true });
        Assert.Equal("ok", await client.GetStringAsync($"http://127.0.0.1:{port}/fixture", timeout.Token)); await served;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("168.63.129.16")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:a9fe:a9fe::1")]
    public async Task SensitiveAddressesFailClosedEvenWithAllowRule(string host)
    {
        var events = new ConcurrentBag<string>();
        var profile = Profile(new PublishedProxyRule { Kind = DestinationKind.Ip, Destination = host, AllowPrivateAddresses = true });
        await using var proxy = new PublishedProxyService(profile, (code, _) => events.Add(code)); proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
        Assert.Equal(2, await Socks(client.GetStream(), host, 443, timeout.Token));
        Assert.Contains("HOST_PROXY_DENIED", events); Assert.DoesNotContain("HOST_PROXY_CONNECTED", events);
        Assert.All(events, value => Assert.DoesNotContain(host, value));
    }

    [Fact]
    public async Task DomainRuleCannotReachLoopbackThroughDnsOrRebinding()
    {
        var resolutions = 0;
        var profile = Profile(new PublishedProxyRule { Kind = DestinationKind.Domain, Destination = "fixture.invalid", AllowPrivateAddresses = true });
        await using var proxy = new PublishedProxyService(profile, (_, _) => { }, (_, _) =>
        { Interlocked.Increment(ref resolutions); return Task.FromResult(new[] { IPAddress.Parse("203.0.113.1"), IPAddress.Loopback }); });
        proxy.Start(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
        Assert.Equal(2, await Socks(client.GetStream(), "fixture.invalid", 443, timeout.Token)); Assert.Equal(1, resolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreachableFirstAddressFallsBackOnlyToAlreadyValidatedIp(bool http)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = IPAddress.Parse("2001:db8::1"); var second = IPAddress.Parse("203.0.113.10");
        var attempts = new ConcurrentQueue<IPAddress>(); var resolutions = 0;
        var profile = Profile(new PublishedProxyRule { Kind = DestinationKind.Domain, Destination = "fixture.invalid", Ports = [port] });
        await using var proxy = new PublishedProxyService(profile, (_, _) => { },
            (_, _) => { Interlocked.Increment(ref resolutions); return Task.FromResult(new[] { first, second }); },
            async (address, requestedPort, ct) =>
            {
                attempts.Enqueue(address); Assert.Equal(port, requestedPort);
                if (address.Equals(first)) throw new SocketException((int)SocketError.NetworkUnreachable);
                Assert.Equal(second, address);
                // The deterministic connector emulates an approved public endpoint using a local fixture.
                // Production uses the pinned IP directly; no DNS, public traffic or route changes in this test.
                var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, port, ct); return peer;
            });
        proxy.Start();
        var fixture = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            var request = new byte[4]; await peer.GetStream().ReadExactlyAsync(request, timeout.Token);
            Assert.Equal("ping", Encoding.ASCII.GetString(request));
            await peer.GetStream().WriteAsync("pong"u8.ToArray(), timeout.Token);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == (http ? HostPortKind.HttpProxy : HostPortKind.Socks5Proxy)).LocalPort, timeout.Token);
        if (http)
        {
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT fixture.invalid:{port} HTTP/1.1\r\nHost: fixture.invalid:{port}\r\n\r\n"), timeout.Token);
            Assert.StartsWith("HTTP/1.1 200", await Header(client.GetStream(), timeout.Token));
        }
        else Assert.Equal(0, await Socks(client.GetStream(), "fixture.invalid", port, timeout.Token));
        await client.GetStream().WriteAsync("ping"u8.ToArray(), timeout.Token);
        var reply = new byte[4]; await client.GetStream().ReadExactlyAsync(reply, timeout.Token);
        Assert.Equal("pong", Encoding.ASCII.GetString(reply)); await fixture;
        Assert.Equal(new[] { first, second }, attempts.ToArray()); Assert.Equal(1, resolutions); Assert.False(origin.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteDestinationMayUseTheSamePortAsAnUnrelatedLocalService(bool http)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var fixturePort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var remote = IPAddress.Parse("203.0.113.20");
        var profile = Profile(new PublishedProxyRule { Kind = DestinationKind.Domain, Destination = "fixture.invalid", Ports = [443] });
        profile = profile with { Ports = [.. profile.Ports, new() { Kind = HostPortKind.Service, RemotePort = 8443, LocalPort = 443 }] };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var attempts = 0;
        await using var proxy = new PublishedProxyService(profile, (_, _) => { },
            (_, _) => Task.FromResult(new[] { remote }), async (address, port, ct) =>
            {
                Assert.Equal(remote, address); Assert.Equal(443, port); Interlocked.Increment(ref attempts);
                var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, fixturePort, ct); return peer;
            });
        proxy.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            await peer.GetStream().WriteAsync("same-port"u8.ToArray(), timeout.Token);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == (http ? HostPortKind.HttpProxy : HostPortKind.Socks5Proxy)).LocalPort, timeout.Token);
        if (http)
        {
            await client.GetStream().WriteAsync("CONNECT fixture.invalid:443 HTTP/1.1\r\nHost: fixture.invalid:443\r\n\r\n"u8.ToArray(), timeout.Token);
            Assert.StartsWith("HTTP/1.1 200", await Header(client.GetStream(), timeout.Token));
        }
        else Assert.Equal(0, await Socks(client.GetStream(), "fixture.invalid", 443, timeout.Token));
        var response = new byte[9]; await client.GetStream().ReadExactlyAsync(response, timeout.Token);
        Assert.Equal("same-port", Encoding.ASCII.GetString(response)); Assert.Equal(1, attempts); await serving;
    }

    [Fact]
    public async Task DefaultDenyAndPortRestrictionsDoNotReachLocalFixture()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        foreach (var rules in new[] { Array.Empty<PublishedProxyRule>(), new[] { Loopback(port == 443 ? 80 : 443) } })
        {
            await using var proxy = new PublishedProxyService(Profile(rules), (_, _) => { }); proxy.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
            Assert.Equal(2, await Socks(client.GetStream(), "127.0.0.1", port, timeout.Token)); Assert.False(origin.Pending());
        }
    }

    [Fact]
    public async Task ProxyCannotConnectBackToItsOwnListeners()
    {
        await using var proxy = new PublishedProxyService(Profile(Loopback(443) with { Ports = [] }), (_, _) => { }); proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); var port = proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort;
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        Assert.Equal(2, await Socks(client.GetStream(), "127.0.0.1", port, timeout.Token));
    }

    [Fact]
    public async Task IndependentProxyServicesCannotConnectToEachOthersListeners()
    {
        var profile = Profile(Loopback(443) with { Ports = [] });
        await using var first = new PublishedProxyService(profile, (_, _) => { }); first.Start();
        await using var second = new PublishedProxyService(profile with { Id = Guid.NewGuid().ToString("N") }, (_, _) => { }); second.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var destination in second.Ports)
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, first.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
            Assert.Equal(2, await Socks(client.GetStream(), "127.0.0.1", destination.LocalPort, timeout.Token));
        }
    }

    [Fact]
    public async Task RawPublicationCannotReservePublicOrInternalProxyListeners()
    {
        var proxy = new PublishedProxyService(Profile(), (_, _) => { }); proxy.Start();
        int[] registered;
        try
        {
            // Inspect only this fixture's registrations so parallel network tests cannot
            // affect the assertion. The public API intentionally does not list internals.
            registered = ((HashSet<int>)typeof(PublishedProxyService).GetField("registeredPorts",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(proxy)!).ToArray();
            Assert.True(registered.Length > proxy.Ports.Length);
            foreach (var port in registered)
            {
                Assert.True(PublishedProxyService.IsReservedLocalPort(port));
                Assert.Equal("SERVICE_BINDING_REQUIRED", Assert.Throws<ToolkitException>(() => LocalServiceEndpoints.ReserveExternalPorts([port])).Code);
            }
        }
        finally { await proxy.DisposeAsync(); }
        foreach (var port in registered) Assert.False(PublishedProxyService.IsReservedLocalPort(port));
    }

    [Fact]
    public async Task RawPublicationReservationPreventsLaterProxyStartAndIsReleased()
    {
        using var selection = new TcpListener(IPAddress.Loopback, 0); selection.Start();
        var port = ((IPEndPoint)selection.LocalEndpoint).Port; selection.Stop();
        var profile = Profile() with { Ports = [new() { Kind = HostPortKind.Socks5Proxy, RemotePort = 3180, LocalPort = port }] };
        using (LocalServiceEndpoints.ReserveExternalPorts([port]))
        {
            var factory = new PublishedProxyFactory((_, _) => { });
            Assert.Equal("SERVICE_BINDING_REQUIRED", (await Assert.ThrowsAsync<ToolkitException>(async () => await factory.StartAsync(profile, CancellationToken.None))).Code);
            Assert.False(PublishedProxyService.IsReservedLocalPort(port));
        }
        await using var running = await new PublishedProxyFactory((_, _) => { }).StartAsync(profile, CancellationToken.None);
        Assert.Equal(port, running.Ports.Single().LocalPort);
    }

    [Fact]
    public void ExternalReservationsAreAtomicReferenceCountedAndIdempotent()
    {
        using var firstPort = new TcpListener(IPAddress.Loopback, 0); firstPort.Start();
        using var secondPort = new TcpListener(IPAddress.Loopback, 0); secondPort.Start();
        var first = ((IPEndPoint)firstPort.LocalEndpoint).Port;
        var second = ((IPEndPoint)secondPort.LocalEndpoint).Port;
        var firstLease = LocalServiceEndpoints.ReserveExternalPorts([first, first]);
        var secondLease = LocalServiceEndpoints.ReserveExternalPorts([first]);
        try
        {
            Assert.False(LocalServiceEndpoints.TryRegisterManagedPort(first));
            firstLease.Dispose(); firstLease.Dispose();
            Assert.False(LocalServiceEndpoints.TryRegisterManagedPort(first));
        }
        finally { firstLease.Dispose(); secondLease.Dispose(); }
        Assert.True(LocalServiceEndpoints.TryRegisterManagedPort(first));
        try
        {
            Assert.Throws<ToolkitException>(() => LocalServiceEndpoints.ReserveExternalPorts([second, first]));
            Assert.True(LocalServiceEndpoints.TryRegisterManagedPort(second));
            LocalServiceEndpoints.UnregisterManagedPort(second);
        }
        finally { LocalServiceEndpoints.UnregisterManagedPort(first); }
    }

    [Fact]
    public async Task HttpConnectSupportsServerFirstAndRefusesDeniedTargetBeforeSuccess()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var proxy = new PublishedProxyService(Profile(Loopback(port)), (_, _) => { }); proxy.Start();
        var fixture = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            await peer.GetStream().WriteAsync("ready"u8.ToArray(), timeout.Token);
        });
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.HttpProxy).LocalPort, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"), timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", await Header(client.GetStream(), timeout.Token));
        var greeting = new byte[5]; await client.GetStream().ReadExactlyAsync(greeting, timeout.Token);
        Assert.Equal("ready", Encoding.ASCII.GetString(greeting)); await fixture;
        using var blocked = new TcpClient(); await blocked.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.HttpProxy).LocalPort, timeout.Token);
        await blocked.GetStream().WriteAsync("CONNECT 169.254.169.254:80 HTTP/1.1\r\nHost: 169.254.169.254:80\r\n\r\n"u8.ToArray(), timeout.Token);
        Assert.StartsWith("HTTP/1.1 403", await Header(blocked.GetStream(), timeout.Token));
    }

    [Fact]
    public async Task HttpConnectCanReachExplicitIpv6LoopbackFixture()
    {
        using var origin = new TcpListener(IPAddress.IPv6Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var proxy = new PublishedProxyService(Profile(Loopback(port) with { Destination = "::1" }), (_, _) => { }); proxy.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            await peer.GetStream().WriteAsync("ipv6"u8.ToArray(), timeout.Token);
        });
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.HttpProxy).LocalPort, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT [::1]:{port} HTTP/1.1\r\nHost: [::1]:{port}\r\n\r\n"), timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", await Header(client.GetStream(), timeout.Token));
        var message = new byte[4]; await client.GetStream().ReadExactlyAsync(message, timeout.Token);
        Assert.Equal("ipv6", Encoding.ASCII.GetString(message)); await serving;
    }

    [Theory]
    [InlineData("CONNECT 127.0.0.1:80/path HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT user@127.0.0.1:80 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT 127.0.0.1:80 HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("CONNECT 127.0.0.1:80 HTTP/1.1\r\nContent-Length: 100\r\n\r\n")]
    public async Task HttpConnectRejectsAmbiguousAuthorityOrBody(string request)
    {
        await using var proxy = new PublishedProxyService(Profile(Loopback(80)), (_, _) => { }); proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.HttpProxy).LocalPort, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        Assert.StartsWith("HTTP/1.1 403", await Header(client.GetStream(), timeout.Token));
    }

    [Fact]
    public async Task OnlyTcpConnectIsSupportedAndDisposalClosesWaitingClients()
    {
        var proxy = new PublishedProxyService(Profile(), (_, _) => { }); proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
        Assert.Equal(7, await Socks(client.GetStream(), "example.org", 443, timeout.Token, command: 3));
        using var waiting = new TcpClient(); await waiting.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
        await proxy.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.Equal(0, await waiting.GetStream().ReadAsync(new byte[1], timeout.Token));
    }

    [Fact]
    public void InvalidMappingsAndImplicitDomainLoopbackBypassAreRejected()
    {
        Assert.Throws<ToolkitException>(() => HostingValidation.Profile(Profile() with { Ports = [new() { Kind = HostPortKind.Service, LocalPort = 0 }] }));
        Assert.Throws<ToolkitException>(() => HostingValidation.Profile(Profile() with { Ports = [new() { LocalAddress = "0.0.0.0" }] }));
        Assert.Throws<ToolkitException>(() => HostingValidation.Profile(Profile(new PublishedProxyRule { Destination = "example.org", AllowLoopback = true })));
        HostingValidation.Profile(Profile(new PublishedProxyRule { Destination = "example.org,example.net\nexample.com", Ports = [443, 80] }));
    }

    [Fact]
    public async Task OrderedRulesUseFirstEnabledMatchWithoutCombiningPermissions()
    {
        var allow = new PublishedProxyRule { Destination = "fixture.invalid", Action = PublishedProxyAction.Allow };
        var deny = allow with { Id = Guid.NewGuid().ToString("N"), Action = PublishedProxyAction.Deny };
        Assert.Equal(2, await PolicyResult(new() { Rules = [deny, allow] }));
        Assert.Equal(4, await PolicyResult(new() { Rules = [allow, deny] }));
        Assert.Equal(4, await PolicyResult(new() { Rules = [deny with { Enabled = false }, allow] }));
        Assert.Equal(2, await PolicyResult(new() { Rules = [allow with { Enabled = false }] }));
        Assert.Equal(2, await PolicyResult(new()
        {
            Rules = [allow, allow with
        { Id = Guid.NewGuid().ToString("N"), AllowPrivateAddresses = true }]
        }, resolved: "10.10.0.10"));
        Assert.Equal(4, await PolicyResult(new()
        {
            Rules = [allow with { AllowPrivateAddresses = true }, allow with
        { Id = Guid.NewGuid().ToString("N") }]
        }, resolved: "10.10.0.10"));
    }

    [Fact]
    public async Task DestinationListsAndRulePortRangesShareTheSameAction()
    {
        var range = new ProxyPortRange { Start = 8000, End = 8090 };
        var rule = new PublishedProxyRule { Destination = "other.invalid,fixture.invalid\nthird.invalid", Ports = [443], PortRanges = [range] };
        Assert.Equal(4, await PolicyResult(new() { Rules = [rule] }));
        Assert.Equal(4, await PolicyResult(new() { Rules = [rule] }, port: 8000));
        Assert.Equal(4, await PolicyResult(new() { Rules = [rule] }, port: 8090));
        Assert.Equal(2, await PolicyResult(new() { Rules = [rule] }, port: 8091));
        Assert.Equal(2, await PolicyResult(new() { Rules = [rule with { Action = PublishedProxyAction.Deny }], AllowAllDestinations = true }));
    }

    [Fact]
    public async Task GeneralAndConnectPortFiltersAreIntersectedForSocks()
    {
        var policy = new PublishedProxyPolicy
        {
            AllowAllDestinations = true,
            AllowedPorts = [80],
            AllowedPortRanges = [new() { Start = 400, End = 500 }],
            ConnectPorts = [443],
            ConnectPortRanges = [new() { Start = 8000, End = 8090 }]
        };
        Assert.Equal(4, await PolicyResult(policy));
        Assert.Equal(2, await PolicyResult(policy, port: 80));
        Assert.Equal(2, await PolicyResult(policy, port: 8001));
        Assert.Equal(4, await PolicyResult(policy with { AllowedPortRanges = [new() { Start = 8000, End = 8090 }] }, port: 8001));
    }

    [Theory]
    [InlineData("203.0.113.10", 4)]
    [InlineData("2001:db8::10", 4)]
    [InlineData("10.10.0.10", 2)]
    [InlineData("127.0.0.1", 2)]
    [InlineData("::1", 2)]
    [InlineData("fd00::1", 2)]
    [InlineData("169.254.169.254", 2)]
    [InlineData("168.63.129.16", 2)]
    [InlineData("100.100.100.200", 2)]
    [InlineData("fd00:ec2::254", 2)]
    public async Task AllowAllFallbackOnlyAllowsPublicDestinations(string address, int expected)
    {
        Assert.Equal(expected, await PolicyResult(new() { AllowAllDestinations = true }, host: address, resolved: address));
        Assert.Equal(expected, await PolicyResult(new() { AllowAllDestinations = true }, resolved: address));
    }

    [Fact]
    public async Task GeneralHttpPortsDoNotGrantConnectOrSocksPermission()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var connectPort = port == 443 ? 8443 : 443;
        var profile = Profile(Loopback(port));
        profile = profile with { Proxy = profile.Proxy with { AllowedPorts = [port], ConnectPorts = [connectPort] } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var proxy = new PublishedProxyService(profile, (_, _) => { }); proxy.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            Assert.StartsWith("GET /fixture HTTP/1.1", await Header(peer.GetStream(), timeout.Token));
            await peer.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), timeout.Token);
        });
        var httpPort = proxy.Ports.Single(p => p.Kind == HostPortKind.HttpProxy).LocalPort;
        using var http = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"), UseProxy = true });
        Assert.Equal("ok", await http.GetStringAsync($"http://127.0.0.1:{port}/fixture", timeout.Token)); await serving;
        using var connect = new TcpClient(); await connect.ConnectAsync(IPAddress.Loopback, httpPort, timeout.Token);
        await connect.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"), timeout.Token);
        Assert.StartsWith("HTTP/1.1 403", await Header(connect.GetStream(), timeout.Token));
        using var socks = new TcpClient(); await socks.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
        Assert.Equal(2, await Socks(socks.GetStream(), "127.0.0.1", port, timeout.Token));
        Assert.False(origin.Pending());
    }

    [Fact]
    public async Task HttpKeepAliveCannotBypassConnectPolicy()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var profile = Profile(Loopback(port));
        profile = profile with { Proxy = profile.Proxy with { ConnectPorts = [port == 443 ? 8443 : 443] } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var proxy = new PublishedProxyService(profile, (_, _) => { }); proxy.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            await Header(peer.GetStream(), timeout.Token);
            await peer.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(), timeout.Token);
        });
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.HttpProxy).LocalPort, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{port}/fixture HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"), timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", await Header(stream, timeout.Token));
        var body = new byte[2]; await stream.ReadExactlyAsync(body, timeout.Token); Assert.Equal("ok", Encoding.ASCII.GetString(body)); await serving;
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"), timeout.Token);
        Assert.StartsWith("HTTP/1.1 403", await Header(stream, timeout.Token));
        Assert.False(origin.Pending());
    }

    [Fact]
    public void LegacyPolicyDefaultsAndInvalidRangesAreValidated()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PublishedProxyPolicy>("{\"Rules\":[{\"Destination\":\"example.org\"}]}")!;
        Assert.True(legacy.Rules[0].Enabled); Assert.Equal(PublishedProxyAction.Allow, legacy.Rules[0].Action);
        Assert.False(legacy.AllowAllDestinations); Assert.Empty(legacy.AllowedPorts); Assert.Empty(legacy.ConnectPorts);
        HostingValidation.Policy(legacy);
        foreach (var range in new[] { new ProxyPortRange { Start = 0, End = 80 }, new() { Start = 90, End = 80 }, new() { Start = 80, End = 65536 } })
        {
            Assert.Throws<ToolkitException>(() => HostingValidation.Policy(new() { AllowedPortRanges = [range] }));
            Assert.Throws<ToolkitException>(() => HostingValidation.Policy(new() { ConnectPortRanges = [range] }));
            Assert.Throws<ToolkitException>(() => HostingValidation.Policy(new() { Rules = [new() { Destination = "example.org", PortRanges = [range] }] }));
        }
        Assert.Throws<ToolkitException>(() => HostingValidation.Policy(new() { AllowedPorts = null! }));
        Assert.Throws<ToolkitException>(() => HostingValidation.Policy(new() { ConnectPortRanges = null! }));
        Assert.Throws<ToolkitException>(() => HostingValidation.Policy(new() { Rules = [new() { Destination = "example.org", Action = (PublishedProxyAction)999 }] }));
        var repeated = new PublishedProxyRule { Destination = "example.org" };
        Assert.Throws<ToolkitException>(() => HostingValidation.Policy(new() { Rules = [repeated, repeated] }));
        var serviceId = new string('a', 32);
        HostingValidation.Profile(Profile() with { Ports = [new() { Kind = HostPortKind.HttpProxy, ServiceId = serviceId }] });
        Assert.Throws<ToolkitException>(() => HostingValidation.Profile(Profile() with { Ports = [new() { Kind = HostPortKind.Service, LocalPort = 3140, ServiceId = serviceId }] }));
    }

    // An approved destination reaches the injected connector and fails as unreachable (4).
    // A denied destination is rejected earlier (2), without any real public traffic.
    private static async Task<int> PolicyResult(PublishedProxyPolicy policy, string host = "fixture.invalid", string resolved = "203.0.113.10", int port = 443)
    {
        var profile = Profile() with { Proxy = policy };
        await using var proxy = new PublishedProxyService(profile, (_, _) => { }, (_, _) => Task.FromResult(new[] { IPAddress.Parse(resolved) }),
            (_, _, _) => throw new SocketException((int)SocketError.HostUnreachable));
        proxy.Start(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Ports.Single(p => p.Kind == HostPortKind.Socks5Proxy).LocalPort, timeout.Token);
        return await Socks(client.GetStream(), host, port, timeout.Token);
    }

    private static async Task<int> Socks(Stream stream, string host, int port, CancellationToken token, byte command = 1)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token); var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
        Assert.Equal(new byte[] { 5, 0 }, greeting);
        var name = Encoding.ASCII.GetBytes(host);
        await stream.WriteAsync(new byte[] { 5, command, 0, 3, (byte)name.Length }.Concat(name).Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray(), token);
        var reply = new byte[10]; await stream.ReadExactlyAsync(reply, token); return reply[1];
    }

    private static async Task<string> Header(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(); var single = new byte[1];
        while (buffer.Count < 16384)
        {
            await stream.ReadExactlyAsync(single, token); buffer.Add(single[0]);
            if (buffer.Count >= 4 && buffer.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) return Encoding.ASCII.GetString(buffer.ToArray());
        }
        throw new InvalidDataException();
    }
}
