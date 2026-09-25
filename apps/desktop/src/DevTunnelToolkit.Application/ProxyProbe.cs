using System.Diagnostics;
using System.Net;
using System.Net.Security;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

/// <summary>Read-only HEAD through the running proxy, with normal TLS validation and no redirects.</summary>
public static class ProxyProbe
{
    public static async Task<ProxyProbeResult> RunAsync(AgentSnapshot state, string host, int port, bool https, CancellationToken cancellationToken, bool upstreamOnly = false)
    {
        var route = ProxyRouting.Decide(state, host, port);
        if (!state.Settings.Proxy.Enabled || state.ProxyErrorCode is not null) throw new ToolkitException("LOCAL_PROXY_NOT_RUNNING");
        var clock = Stopwatch.StartNew();
        ProxyProbeResult Result(bool success, string code, int? status = null) => new(route, success, code, status, clock.ElapsedMilliseconds, !upstreamOnly);
        if (route.Action == RouteAction.Block) return Result(false, route.Reason ?? "PROXY_ROUTE_BLOCKED");
        var proxyUri = new UriBuilder("http", state.Settings.Proxy.ListenAddress, state.Settings.Proxy.Port).Uri;
        if (upstreamOnly)
        {
            if (route.Action != RouteAction.Tunnel || route.Upstream is not { } mapping) throw new ToolkitException("PROXY_UPSTREAM_NOT_AVAILABLE");
            proxyUri = new UriBuilder(route.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http", mapping.LocalAddress, mapping.LocalPort).Uri;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(30, state.Settings.Proxy.ConnectTimeoutSeconds + 10)));
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxyUri),
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(state.Settings.Proxy.ConnectTimeoutSeconds),
            Credentials = null,
            SslOptions = new SslClientAuthenticationOptions() // No TLS bypass or client credentials.
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Head, new UriBuilder(https ? "https" : "http", route.Host, route.Port, "/").Uri)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.ConnectionClose = true;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            // A 403/407/502/504 may be from the upstream proxy, not the destination.
            return Result(status is >= 200 and < 400, status switch
            {
                401 or 403 => "PROXY_PROBE_ACCESS_DENIED",
                407 => "PROXY_UPSTREAM_AUTH_REQUIRED",
                502 or 503 or 504 => "PROXY_PROBE_GATEWAY_ERROR",
                _ => "PROXY_PROBE_HTTP_RESPONSE"
            }, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Result(false, "PROXY_PROBE_TIMEOUT"); }
        catch (HttpRequestException error)
        {
            return Result(false, error.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => "PROXY_PROBE_TLS_FAILED",
                HttpRequestError.ProxyTunnelError => "PROXY_PROBE_CONNECT_FAILED",
                HttpRequestError.NameResolutionError => "PROXY_PROBE_DNS_FAILED",
                _ => "PROXY_PROBE_CONNECTION_FAILED"
            }, (int?)error.StatusCode);
        }
    }
}
