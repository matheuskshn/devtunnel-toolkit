using System.Net;
using DevTunnelToolkit.Core;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace DevTunnelToolkit.Application;

/// <summary>Explicit loopback proxy. No MITM, certificates, OS proxy changes or system credentials.</summary>
public sealed class LocalProxyService(Func<AgentSnapshot> snapshot, Action<string, string?> report) : IAsyncDisposable
{
    private sealed class Attempt(RouteDecision decision)
    {
        public RouteDecision Decision { get; } = decision;
        public bool FellBack { get; set; }
    }

    private ProxyServer? server;
    public int ActiveConnections => server?.AdmittedClientConnectionCount ?? 0;

    public void Start(LocalProxySettings settings)
    {
        Validation.Proxy(settings);
        var instance = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false, trustRootCertificateAsAdmin: false)
        {
            ForwardToUpstreamGateway = false,
            EnableWinAuth = false,
            EnableConnectionPool = false,
            NetworkFailureRetryAttempts = 0,
            EnableHttp2 = false,
            MaxConcurrentClientConnections = settings.MaxClients,
            ClientHeaderTimeoutSeconds = 10,
            ConnectTimeOutSeconds = settings.ConnectTimeoutSeconds,
            ResponseHeaderTimeoutSeconds = settings.IdleTimeoutSeconds,
            IdleReadTimeoutSeconds = settings.IdleTimeoutSeconds,
            IdleWriteTimeoutSeconds = settings.IdleTimeoutSeconds
        };
        instance.Logging.Enabled = false; // URLs, credentials and parser exception details must not reach logs.
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Parse(settings.ListenAddress), settings.Port, decryptSsl: false);
        endpoint.BeforeTunnelConnectRequest += (_, args) =>
        {
            if (!Prepare(args)) args.DenyConnect = true;
            return Task.CompletedTask;
        };
        instance.BeforeRequest += (_, args) =>
        {
            if (!Prepare(args)) args.GenericResponse("Destination blocked by local routing policy.", HttpStatusCode.Forbidden);
            return Task.CompletedTask;
        };
        instance.GetCustomUpStreamProxyFunc = args => Task.FromResult(args.CustomUpStreamProxy);
        instance.CustomUpStreamProxyFailureFunc = args =>
        {
            // Titanium invokes this hook only if upstream TCP establishment failed,
            // before HTTP/CONNECT/SOCKS request bytes. Never retry after a 403/407 or a sent body.
            if (args.UserData is not Attempt { FellBack: false, Decision: { Action: RouteAction.Tunnel, DirectFallback: true } } attempt)
                return Task.FromResult<IExternalProxy?>(null);
            attempt.FellBack = true;
            report("PROXY_DIRECT_FALLBACK", attempt.Decision.ProfileId);
            // The library explicitly treats proxy == destination as a direct connection.
            // A non-null value requests one retry; null means propagate the original error.
            return Task.FromResult<IExternalProxy?>(new ExternalProxy(attempt.Decision.Host, attempt.Decision.Port));
        };
        instance.AddEndPoint(endpoint);
        try { instance.Start(changeSystemProxySettings: false); server = instance; }
        catch { instance.Dispose(); throw new ToolkitException("LOCAL_PROXY_START_FAILED"); }
    }

    private bool Prepare(SessionEventArgsBase args)
    {
        try
        {
            var state = snapshot();
            var uri = args.HttpClient.Request.RequestUri;
            var decision = ProxyRouting.Decide(state, uri.IdnHost, uri.Port);
            args.UserData = new Attempt(decision);
            args.CustomUpStreamProxy = decision.Action == RouteAction.Tunnel && decision.Upstream is { } mapping
                ? new ExternalProxy(mapping.LocalAddress, mapping.LocalPort)
                {
                    ProxyType = decision.Protocol == ProxyProtocol.Socks5 ? ExternalProxyType.Socks5 : ExternalProxyType.Http,
                    ProxyDnsRequests = true,
                    BypassLocalhost = false,
                    UseDefaultCredentials = false
                }
                : null;
            report(decision.Reason ?? $"PROXY_ROUTE_{decision.Action.ToString().ToUpperInvariant()}", decision.ProfileId);
            return decision.Action != RouteAction.Block;
        }
        catch
        {
            report("PROXY_ROUTE_REJECTED", null);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (server is not { } instance) return;
        server = null;
        try { await instance.StopAsync(TimeSpan.FromSeconds(3)); }
        finally { instance.Dispose(); }
    }
}
