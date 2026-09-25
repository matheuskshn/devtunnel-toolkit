using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class MultiDestinationTests
{
    [Theory]
    [InlineData("example.org, example.net", "example.net")]
    [InlineData("example.org\nexample.net", "api.example.net")]
    [InlineData("example.org,\r\nEXAMPLE.NET\r\n", "example.net")]
    public void AnyDestinationUsesTheSameActionAndPort(string destinations, string host)
    {
        var rule = new ProxyRoute { Destination = destinations, Action = RouteAction.Block, DestinationPort = 443 };
        var settings = new AppSettings { Proxy = new() { Rules = [rule] } };
        Validation.Settings(settings);
        var snapshot = new AgentSnapshot([], [], [], [], settings, new("Test", false, false, false, false), []);
        Assert.Equal(RouteAction.Block, ProxyRouting.Decide(snapshot, host, 443).Action);
        Assert.Equal(rule.Id, ProxyRouting.Decide(snapshot, host, 443).RuleId);
        Assert.Equal(RouteAction.Direct, ProxyRouting.Decide(snapshot, host, 80).Action);
        Assert.Equal(RouteAction.Direct, ProxyRouting.Decide(snapshot, "badexample.net", 443).Action);
    }

    [Theory]
    [InlineData(DestinationKind.Ip, "192.0.2.1, 192.0.2.2", "192.0.2.2")]
    [InlineData(DestinationKind.Ip, "2001:db8::1\n2001:db8::2", "2001:db8::2")]
    [InlineData(DestinationKind.Cidr, "192.0.2.0/24\n198.51.100.0/24", "198.51.100.8")]
    public void IpAndCidrListsUseAnyMatch(DestinationKind kind, string values, string host)
    {
        var rule = new ProxyRoute { Destination = values, Kind = kind, Action = RouteAction.Direct };
        Validation.Proxy(new() { Rules = [rule] });
        Assert.True(ProxyRouting.Matches(rule, host));
        Assert.False(ProxyRouting.Matches(rule, "other.invalid"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\n ,")]
    [InlineData("example.org,https://example.net")]
    [InlineData("example.org,*.example.net")]
    [InlineData("example.org,192.0.2.1")]
    [InlineData("example.org,example.net/path")]
    public void InvalidMemberRejectsTheWholeRule(string value) =>
        Assert.Throws<ToolkitException>(() => Validation.Proxy(new() { Rules = [new() { Destination = value, Action = RouteAction.Direct }] }));

    [Fact]
    public void DestinationsShareTheSameTunnelMappingAndFallback()
    {
        var id = Guid.NewGuid().ToString("N");
        var mapping = new EffectiveMapping(3140, "127.0.0.1", 19000);
        var rule = new ProxyRoute { Destination = "one.invalid,two.invalid", ProfileId = id, DirectFallback = true };
        var settings = new AppSettings { Proxy = new() { Rules = [rule] } };
        var state = new AgentSnapshot([], [], [new(id, ConnectionState.Connected, Ports: [mapping])], [], settings, new("Test", false, false, false, false), []);
        foreach (var host in new[] { "one.invalid", "two.invalid" })
        {
            var decision = ProxyRouting.Decide(state, host, 443);
            Assert.Equal(RouteAction.Tunnel, decision.Action); Assert.Same(mapping, decision.Upstream);
            Assert.Equal(id, decision.ProfileId); Assert.True(decision.DirectFallback);
            Assert.Equal(RouteAction.Direct, ProxyRouting.Decide(state with { Connections = [] }, host, 443).Action);
        }
    }

    [Fact]
    public void ListsAreBoundedAndDuplicateEntriesDoNotExpandRouting()
    {
        Assert.Equal(new[] { "example.org", "example.net" }, ProxyRouting.Destinations("example.org, EXAMPLE.ORG\n example.net"));
        var rule = new ProxyRoute { Destination = string.Join(',', Enumerable.Range(0, 101).Select(i => $"host{i}.invalid")), Action = RouteAction.Direct };
        Assert.Throws<ToolkitException>(() => Validation.Proxy(new() { Rules = [rule] }));
        var hundred = string.Join(',', Enumerable.Range(0, 100).Select(i => $"host{i}.invalid"));
        Assert.Throws<ToolkitException>(() => Validation.Proxy(new() { Rules = Enumerable.Range(0, 11).Select(_ => new ProxyRoute { Destination = hundred, Action = RouteAction.Direct }).ToArray() }));
    }
}
