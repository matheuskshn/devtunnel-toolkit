# Local proxy assessment

The desktop currently maps remote TCP ports to loopback listeners. If a tunnel
hosts Squid or Tinyproxy on port 3140, mapping that port to local 3140 already
gives applications a local HTTP/CONNECT proxy. The PAC server can select a mapped
remote HTTP or SOCKS5 proxy by destination domain. The desktop does not run a
proxy server today.

Starting a new proxy on the desktop is feasible, but it changes the egress
point: a proxy that connects directly to destinations uses the desktop's own
network. It does **not** gain the network reachability of the tunnel host.
Remote-network access still needs a remote proxy forwarded through a tunnel, or
another explicit remote egress service.

## Recommended addition

For clients that cannot use PAC, add an opt-in, cross-platform local HTTP proxy
gateway owned by the agent. It would listen on `127.0.0.1` at a configurable
port, defaulting to 3140, and route requests by destination host to the selected
remote proxy mapping. A remote port mapped by the SDK must use a different local
port to avoid a loop. The gateway must fail closed for configured private routes
whose tunnel is unavailable. Direct internet egress would be a separate explicit
choice, never an implicit fallback.

The first scope should be HTTP CONNECT and ordinary HTTP forwarding without TLS
interception or caching. It is not a full Squid replacement. SOCKS5 as a local
listener can follow, sharing the same route policy; forwarding an already-hosted
remote SOCKS5 service requires no new protocol server. Tinyproxy exposes an HTTP
proxy and supports SOCKS5 as an **upstream**, not as its local listener. A bundled
Squid or Tinyproxy subprocess would need separate platform packaging, process
lifecycle, configuration and security gates. Squid can run on Windows but its
Windows build has limitations, so embedding a small managed gateway is a better
cross-platform fit for this narrow use case.

Security requirements: loopback-only binding; explicit route and port allowlists;
bounded headers, connections, buffers and timeouts; no request bodies or
credentials in logs; no arbitrary command or configuration endpoint; no bypass
on upstream failure; hostname matching before routing; and verification that
private names are resolved by the intended remote proxy rather than local DNS.
The trust boundary remains the current OS user, not a multi-user proxy service.

Acceptance tests should cover two simultaneous remote proxies, route selection,
no-direct fallback, connection loss/recovery, DNS behavior, oversized/invalid
requests, HTTP CONNECT byte streams and native Windows/Linux runtime behavior.

References: [Dev Tunnels TCP forwarding](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands),
[Tinyproxy](https://tinyproxy.github.io/),
[Squid on Windows](https://wiki.squid-cache.org/KnowledgeBase/Windows).
