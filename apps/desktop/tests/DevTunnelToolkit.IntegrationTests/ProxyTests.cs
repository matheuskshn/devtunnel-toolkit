using System.Net;
using System.Net.Sockets;
using System.Text;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class ProxyTests
{
    [Fact]
    public async Task ProbeUsesHeadWithoutCredentialsAndDoesNotFollowRedirects()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var state = Snapshot(new() { Enabled = true, Port = FreePort() });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var served = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            var header = await ReadHeaderAsync(peer.GetStream(), timeout.Token);
            Assert.StartsWith("HEAD / HTTP/1.1", header);
            Assert.DoesNotContain("Authorization:", header); Assert.DoesNotContain("Cookie:", header);
            await peer.GetStream().WriteAsync("HTTP/1.1 302 Found\r\nLocation: https://redirect.invalid/\r\nContent-Length: 5000\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
        });
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        var probe = await ProxyProbe.RunAsync(state, "127.0.0.1", ((IPEndPoint)origin.LocalEndpoint).Port, false, timeout.Token);
        Assert.True(probe.Succeeded); Assert.Equal(302, probe.HttpStatus); await served;
    }

    [Fact]
    public async Task ProbeIsBlockedWithoutNetworkAndCannotInjectHeaders()
    {
        var state = Snapshot(new() { Enabled = true, DefaultAction = RouteAction.Block });
        var probe = await ProxyProbe.RunAsync(state, "private.invalid", 443, true, CancellationToken.None);
        Assert.False(probe.Succeeded); Assert.Equal("PROXY_ROUTE_BLOCKED", probe.Code);
        await Assert.ThrowsAsync<ToolkitException>(() => ProxyProbe.RunAsync(state, "example.org\r\nInjected: yes", 443, true, CancellationToken.None));
    }

    [Fact]
    public async Task HttpThroughSocks5ReachesOriginWithoutLocalDns()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var state = SocksState(FreePort(), ((IPEndPoint)upstream.LocalEndpoint).Port, "private.invalid", false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var served = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            var destination = await AcceptSocksAsync(peer.GetStream(), 0, timeout.Token);
            Assert.Equal("private.invalid", destination);
            var request = await ReadHeaderAsync(peer.GetStream(), timeout.Token);
            Assert.StartsWith("HEAD / HTTP/1.1", request);
            await peer.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
        });
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        var result = await ProxyProbe.RunAsync(state, "private.invalid", 80, false, timeout.Token);
        Assert.True(result.Succeeded); Assert.Equal(200, result.HttpStatus); await served;
    }

    [Theory]
    [InlineData(2)] // SOCKS5 connection not allowed by ruleset.
    [InlineData(4)] // Host unreachable is not permission to bypass the remote network.
    public async Task RemoteSocks5ProbeReportsDenialWithoutDirectFallback(int reply)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var state = SocksState(FreePort(), ((IPEndPoint)upstream.LocalEndpoint).Port, "127.0.0.1", true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var served = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            await AcceptSocksAsync(peer.GetStream(), (byte)reply, timeout.Token);
        });
        var events = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using var proxy = new LocalProxyService(() => state, (code, _) => events.Add(code)); proxy.Start(state.Settings.Proxy);
        var pending = ProxyProbe.RunAsync(state, "127.0.0.1", ((IPEndPoint)origin.LocalEndpoint).Port, false, timeout.Token, upstreamOnly: true);
        await served;
        var result = await pending;
        Assert.False(result.Succeeded); Assert.False(result.ThroughLocalProxy);
        Assert.Equal("PROXY_PROBE_CONNECT_FAILED", result.Code);
        Assert.False(origin.Pending()); Assert.DoesNotContain("PROXY_DIRECT_FALLBACK", events);
    }

    private static AgentSnapshot SocksState(int proxyPort, int upstreamPort, string destination, bool fallback)
    {
        var state = TunnelState(proxyPort, upstreamPort, destination, fallback);
        return state with { Settings = state.Settings with { Proxy = state.Settings.Proxy with { Rules = [state.Settings.Proxy.Rules[0] with { Protocol = ProxyProtocol.Socks5 }] } } };
    }

    private static async Task<string> AcceptSocksAsync(Stream stream, byte reply, CancellationToken token)
    {
        var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
        var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, token);
        await stream.WriteAsync(new byte[] { 5, 0 }, token);
        var connect = new byte[4]; await stream.ReadExactlyAsync(connect, token);
        var size = connect[3] switch { 1 => 4, 4 => 16, 3 => 0, _ => throw new InvalidDataException() };
        if (size == 0) { var length = new byte[1]; await stream.ReadExactlyAsync(length, token); size = length[0]; }
        var target = new byte[size + 2]; await stream.ReadExactlyAsync(target, token);
        await stream.WriteAsync(new byte[] { 5, reply, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
        return connect[3] == 3 ? Encoding.ASCII.GetString(target[..^2]) : new IPAddress(target[..^2]).ToString();
    }
    [Fact]
    public async Task ProbeDoesNotClaimSuccessBeforeDestinationResponds()
    {
        var proxyPort = FreePort(); var closedPort = FreePort();
        var state = Snapshot(new() { Enabled = true, Port = proxyPort });
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        var result = await ProxyProbe.RunAsync(state, "127.0.0.1", closedPort, true, CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ConnectThroughSocks5CarriesOpaqueTrafficWithoutLocalDns()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var proxyPort = FreePort();
        var state = TunnelState(proxyPort, ((IPEndPoint)upstream.LocalEndpoint).Port, "private.invalid", false);
        state = state with { Settings = state.Settings with { Proxy = state.Settings.Proxy with { Rules = [state.Settings.Proxy.Rules[0] with { Protocol = ProxyProtocol.Socks5 }] } } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var negotiated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var served = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream(); var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
            Assert.Equal(5, greeting[0]); var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, timeout.Token); Assert.Contains((byte)0, methods);
            await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
            var connect = new byte[5]; await stream.ReadExactlyAsync(connect, timeout.Token);
            Assert.Equal(new byte[] { 5, 1, 0, 3 }, connect[..4]);
            var nameAndPort = new byte[connect[4] + 2]; await stream.ReadExactlyAsync(nameAndPort, timeout.Token);
            Assert.Equal("private.invalid", Encoding.ASCII.GetString(nameAndPort[..^2]));
            Assert.Equal(443, (nameAndPort[^2] << 8) | nameAndPort[^1]);
            negotiated.TrySetResult();
            // Fragment the SOCKS response to exercise real network framing.
            foreach (var value in new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 })
                await stream.WriteAsync(new byte[] { value }, timeout.Token);
            var bytes = new byte[24]; await stream.ReadExactlyAsync(bytes, timeout.Token);
            await stream.WriteAsync(bytes, timeout.Token);
        }, timeout.Token);
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        var local = client.GetStream(); await local.WriteAsync("CONNECT private.invalid:443 HTTP/1.1\r\nHost: private.invalid:443\r\n\r\n"u8.ToArray(), timeout.Token);
        Assert.Contains("200", await ReadHeaderAsync(local, timeout.Token));
        await local.WriteAsync(new byte[24], timeout.Token); var echoed = new byte[24]; await local.ReadExactlyAsync(echoed, timeout.Token);
        await served;
        Assert.True(negotiated.Task.IsCompletedSuccessfully);
    }
    private static AgentSnapshot Snapshot(LocalProxySettings? proxy = null, params ConnectionStatus[] connections) =>
        new([], [], connections, [], new() { Proxy = proxy ?? new() }, new("Test", false, false, false, false), []);

    [Theory]
    [InlineData("example.org", 443, RouteAction.Block)]
    [InlineData("api.example.org", 443, RouteAction.Block)]
    [InlineData("EXAMPLE.ORG.", 443, RouteAction.Block)]
    [InlineData("badexample.org", 443, RouteAction.Direct)]
    [InlineData("example.org.attacker.invalid", 443, RouteAction.Direct)]
    [InlineData("example.org", 80, RouteAction.Direct)]
    public void DomainRulesRespectBoundariesAndPort(string host, int port, RouteAction action)
    {
        var state = Snapshot(new() { Rules = [new() { Destination = "example.org", DestinationPort = 443, Action = RouteAction.Block }] });
        Assert.Equal(action, ProxyRouting.Decide(state, host, port).Action);
    }

    [Theory]
    [InlineData(DestinationKind.Ip, "192.0.2.8", "192.0.2.8", true)]
    [InlineData(DestinationKind.Ip, "192.0.2.8", "192.0.2.9", false)]
    [InlineData(DestinationKind.Cidr, "192.0.2.0/24", "192.0.2.8", true)]
    [InlineData(DestinationKind.Cidr, "192.0.2.0/24", "192.0.3.8", false)]
    [InlineData(DestinationKind.Cidr, "2001:db8::/32", "2001:db8::1", true)]
    [InlineData(DestinationKind.Cidr, "192.0.2.0/24", "example.invalid", false)]
    public void IpAndCidrNeverResolveDomains(DestinationKind kind, string network, string host, bool matched) =>
        Assert.Equal(matched, ProxyRouting.Matches(new() { Kind = kind, Destination = network }, host));

    [Fact]
    public void FirstEnabledRuleWins()
    {
        var rules = new ProxyRoute[] { new() { Destination = "example.org", Action = RouteAction.Block, Enabled = false }, new() { Destination = "example.org", Action = RouteAction.Direct }, new() { Destination = "example.org", Action = RouteAction.Block } };
        Assert.Equal(rules[1].Id, ProxyRouting.Decide(Snapshot(new() { Rules = rules }), "example.org", 443).RuleId);
    }

    [Theory]
    [InlineData(false, RouteAction.Block)]
    [InlineData(true, RouteAction.Direct)]
    public void MissingTunnelOnlyFallsBackWhenExplicit(bool fallback, RouteAction expected)
    {
        var state = Snapshot(new() { Rules = [new() { Destination = "example.org", ProfileId = Guid.NewGuid().ToString("N"), DirectFallback = fallback }] });
        Assert.Equal(expected, ProxyRouting.Decide(state, "example.org", 443).Action);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.0.2.1")]
    public void ProxyCannotBecomeAnOpenLanProxy(string address) =>
        Assert.Throws<ToolkitException>(() => Validation.Proxy(new() { ListenAddress = address }));

    [Fact]
    public void PacSupportsManualAndExplicitFallbackWithoutExecutingJavascript()
    {
        var state = Snapshot();
        const string manual = "function FindProxyForURL(url, host) { return 'PROXY 127.0.0.1:8888; DIRECT'; }\n// fixture";
        var settings = new PacSettings { Mode = PacMode.Manual, ManualScript = manual };
        Validation.Pac(settings);
        Assert.Equal(manual, PacService.Generate(state with { Settings = state.Settings with { Pac = settings } }));
        Assert.Throws<ToolkitException>(() => Validation.Pac(settings with { ManualScript = new string('a', 65537) }));
        var rule = new PacRule { Domain = "example.org", ProfileId = Guid.NewGuid().ToString("N"), RemotePort = 3140, DirectFallback = true };
        var fallback = state with { Settings = state.Settings with { Pac = new() { Rules = [rule] } } };
        Assert.DoesNotContain("127.0.0.1:1", PacService.Generate(fallback));
        Assert.Contains("PROXY 127.0.0.1:19000; DIRECT", PacService.Generate(fallback with { Connections = [new(rule.ProfileId, ConnectionState.Connected, Ports: [new(3140, "127.0.0.1", 19000)])] }));
        var ipv6 = state with { Settings = new() { Proxy = new() { ListenAddress = "::1" }, Pac = new() { Mode = PacMode.LocalProxy } } };
        Assert.Contains("PROXY [::1]:8888", PacService.Generate(ipv6));
    }

    [Fact]
    public async Task DirectHttpRequestReachesOriginAndReleasesPort()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var proxyPort = FreePort();
        var state = Snapshot(new() { Enabled = true, Port = proxyPort });
        var served = ServeResponseAsync(origin, 200, "direct-fixture");
        await using (var proxy = new LocalProxyService(() => state, (_, _) => { }))
        {
            proxy.Start(state.Settings.Proxy);
            using var http = Client(proxyPort);
            Assert.Equal("direct-fixture", await http.GetStringAsync($"http://127.0.0.1:{originPort}/fixture"));
            Assert.StartsWith("GET /fixture HTTP/1.1", await served);
        }
        using var reserved = new TcpListener(IPAddress.Loopback, proxyPort); reserved.Start();
    }

    [Fact]
    public async Task HttpTunnelUsesUpstreamWithoutLocalDnsAndDoesNotFallBackOnDenial()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var proxyPort = FreePort();
        var state = TunnelState(proxyPort, upstreamPort, "private.invalid", true);
        var events = new List<string>();
        await using var proxy = new LocalProxyService(() => state, (code, _) => { lock (events) events.Add(code); }); proxy.Start(state.Settings.Proxy);
        var served = ServeResponseAsync(upstream, 407, "denied");
        using var http = Client(proxyPort);
        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, (await http.GetAsync("http://private.invalid/fixture")).StatusCode);
        Assert.StartsWith("GET http://private.invalid/fixture HTTP/1.1", await served);
        Assert.DoesNotContain("PROXY_DIRECT_FALLBACK", events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureFallsBackOnlyBeforeSendingRequest(bool fallback)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var proxyPort = FreePort(); var unavailablePort = FreePort();
        var state = TunnelState(proxyPort, unavailablePort, "127.0.0.1", fallback);
        var events = new List<string>();
        await using var proxy = new LocalProxyService(() => state, (code, _) => { lock (events) events.Add(code); }); proxy.Start(state.Settings.Proxy);
        using var http = Client(proxyPort);
        if (fallback)
        {
            var served = ServeResponseAsync(origin, 200, "fallback-fixture");
            Assert.Equal("fallback-fixture", await http.GetStringAsync($"http://127.0.0.1:{originPort}/fixture"));
            await served;
            Assert.Contains("PROXY_DIRECT_FALLBACK", events);
        }
        else
        {
            try { var response = await http.GetAsync($"http://127.0.0.1:{originPort}/fixture"); Assert.False(response.IsSuccessStatusCode); }
            catch (HttpRequestException) { }
            Assert.False(origin.Pending());
            Assert.DoesNotContain("PROXY_DIRECT_FALLBACK", events);
        }
    }

    [Fact]
    public async Task ConnectIsOpaqueTcpWithoutTlsInterception()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var proxyPort = FreePort(); var state = Snapshot(new() { Enabled = true, Port = proxyPort });
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"), timeout.Token);
        Assert.Contains("200", await ReadHeaderAsync(stream, timeout.Token));
        var opaque = Encoding.ASCII.GetBytes("\0opaque-tcp-fixture");
        await stream.WriteAsync(opaque, timeout.Token);
        using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
        var received = new byte[opaque.Length]; await peer.GetStream().ReadExactlyAsync(received, timeout.Token);
        Assert.Equal(opaque, received);
    }

    [Fact]
    public async Task BlockAndSelfLoopNeverReachOrigin()
    {
        var proxyPort = FreePort(); var state = Snapshot(new() { Enabled = true, Port = proxyPort, DefaultAction = RouteAction.Block });
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        using var http = Client(proxyPort);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("http://unknown.invalid/fixture")).StatusCode);
        state = state with { Settings = state.Settings with { Proxy = state.Settings.Proxy with { DefaultAction = RouteAction.Direct } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"http://127.0.0.1:{proxyPort}/fixture")).StatusCode);
    }

    [Fact]
    public async Task SocksUpstreamReceivesHostnameWithoutLocalDns()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var proxyPort = FreePort();
        var state = TunnelState(proxyPort, ((IPEndPoint)upstream.LocalEndpoint).Port, "private.invalid", false);
        state = state with { Settings = state.Settings with { Proxy = state.Settings.Proxy with { Rules = [state.Settings.Proxy.Rules[0] with { Protocol = ProxyProtocol.Socks5 }] } } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var served = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream(); var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
            Assert.Equal(5, greeting[0]); var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, timeout.Token);
            Assert.Contains((byte)0, methods); await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
            var request = new byte[5]; await stream.ReadExactlyAsync(request, timeout.Token);
            Assert.Equal(new byte[] { 5, 1, 0, 3 }, request[..4]);
            var host = new byte[request[4]]; await stream.ReadExactlyAsync(host, timeout.Token);
            Assert.Equal("private.invalid", Encoding.ASCII.GetString(host));
            var port = new byte[2]; await stream.ReadExactlyAsync(port, timeout.Token); Assert.Equal(new byte[] { 0, 80 }, port);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, timeout.Token);
            var header = await ReadHeaderAsync(stream, timeout.Token);
            Assert.StartsWith("GET /fixture HTTP/1.1", header);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nsocks"u8.ToArray(), timeout.Token);
        }, timeout.Token);
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        using var http = Client(proxyPort);
        Assert.Equal("socks", await http.GetStringAsync("http://private.invalid/fixture", timeout.Token));
        await served;
    }

    [Fact]
    public async Task AnUpstreamThatReceivedAPostIsNeverRetriedDirectly()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var proxyPort = FreePort();
        var state = TunnelState(proxyPort, ((IPEndPoint)upstream.LocalEndpoint).Port, "127.0.0.1", true);
        var events = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            Assert.StartsWith("POST http://", await ReadHeaderAsync(peer.GetStream(), timeout.Token));
            var body = new byte[7]; await peer.GetStream().ReadExactlyAsync(body, timeout.Token);
            Assert.Equal("fixture", Encoding.ASCII.GetString(body));
            // Close after accepting the body, without an HTTP response.
        }, timeout.Token);
        await using var proxy = new LocalProxyService(() => state, (code, _) => { lock (events) events.Add(code); }); proxy.Start(state.Settings.Proxy);
        using var http = Client(proxyPort);
        try
        {
            var response = await http.PostAsync($"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/fixture", new StringContent("fixture"), timeout.Token);
            Assert.False(response.IsSuccessStatusCode);
        }
        catch (HttpRequestException) { }
        await received;
        Assert.False(origin.Pending()); Assert.False(upstream.Pending());
        Assert.DoesNotContain("PROXY_DIRECT_FALLBACK", events);
    }

    [Fact]
    public async Task ConnectUsesHttpUpstreamWithoutDnsOrDecryptingBytes()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var proxyPort = FreePort();
        var state = TunnelState(proxyPort, ((IPEndPoint)upstream.LocalEndpoint).Port, "private.invalid", true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var served = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            Assert.StartsWith("CONNECT private.invalid:443 HTTP/1.1", await ReadHeaderAsync(peer.GetStream(), timeout.Token));
            await peer.GetStream().WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), timeout.Token);
            var bytes = new byte[24]; await peer.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            Assert.All(bytes, value => Assert.Equal(0, value));
            await peer.GetStream().WriteAsync(bytes, timeout.Token);
        }, timeout.Token);
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync("CONNECT private.invalid:443 HTTP/1.1\r\nHost: private.invalid:443\r\n\r\n"u8.ToArray(), timeout.Token);
        Assert.Contains("200", await ReadHeaderAsync(stream, timeout.Token));
        await stream.WriteAsync(new byte[24], timeout.Token);
        var reply = new byte[24]; await stream.ReadExactlyAsync(reply, timeout.Token);
        await served;
    }

    [Fact]
    public async Task HttpsKeepsTheOriginCertificateEndToEnd()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        // Schannel needs a key container rather than the generated ephemeral CNG key.
        // Keep the synthetic PFX in memory; disposal removes its temporary key container.
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(generated.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port; var proxyPort = FreePort();
        var state = Snapshot(new() { Enabled = true, Port = proxyPort });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var served = Task.Run(async () =>
        {
            using var peer = await origin.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new System.Net.Security.SslStream(peer.GetStream());
            await tls.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            Assert.StartsWith("GET /fixture HTTP/1.1", await ReadHeaderAsync(tls, timeout.Token));
            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nsecure"u8.ToArray(), timeout.Token);
        }, timeout.Token);
        await using var proxy = new LocalProxyService(() => state, (_, _) => { }); proxy.Start(state.Settings.Proxy);
        var sawOriginCertificate = false;
        using var http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            SslOptions = new()
            {
                // Pin this isolated fixture's certificate, not a permissive TLS bypass.
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    sawOriginCertificate = presented is not null && presented.GetRawCertData().SequenceEqual(certificate.RawData)
            }
        });
        var response = http.GetStringAsync($"https://127.0.0.1:{originPort}/fixture", timeout.Token);
        await Task.WhenAll(served, response);
        Assert.Equal("secure", await response);
        Assert.True(sawOriginCertificate);
    }

    private static AgentSnapshot TunnelState(int proxyPort, int upstreamPort, string destination, bool fallback)
    {
        var id = Guid.NewGuid().ToString("N");
        var rule = new ProxyRoute { Destination = destination, Kind = IPAddress.TryParse(destination, out _) ? DestinationKind.Ip : DestinationKind.Domain, ProfileId = id, DirectFallback = fallback };
        return Snapshot(new() { Enabled = true, Port = proxyPort, Rules = [rule] }, new ConnectionStatus(id, ConnectionState.Connected, Ports: [new(3140, "127.0.0.1", upstreamPort)]));
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static HttpClient Client(int port) => new(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private static async Task<string> ServeResponseAsync(TcpListener listener, int status, string body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var header = await ReadHeaderAsync(peer.GetStream(), timeout.Token);
        await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), timeout.Token);
        return header;
    }
    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new byte[1];
        while (result.Length < 16384)
        {
            await stream.ReadExactlyAsync(buffer, token); result.Append((char)buffer[0]);
            if (result.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return result.ToString();
        }
        throw new InvalidDataException("Fixture header too large.");
    }
}
