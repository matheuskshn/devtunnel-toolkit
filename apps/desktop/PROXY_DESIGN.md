# Desktop proxy roles

The Desktop offers two separate proxy functions. Neither is a VPN, transparent
system-wide redirector or complete Squid replacement. Applications must use an
HTTP/SOCKS proxy or a mapped TCP port explicitly.

| Role | Personal selective proxy | Managed proxy service |
| --- | --- | --- |
| Purpose | Route this computer's applications | Offer this computer's permitted network access locally or through explicit tunnel bindings |
| Interface | Proxy e PAC | Serviços; optional publication in DevTunnel |
| Ingress | Loopback HTTP/CONNECT, optional PAC | Independent loopback HTTP/CONNECT or SOCKS5 listener |
| Routing | Ordered Direct/Tunnel/Block rules | Ordered enabled Allow/Deny rules, default deny, optional explicit public-destination fallback |
| Tunnel failure | Per-rule direct fallback opt-in | Service stays running; publication reconnects independently; no route fallback |
| Egress | Local network or selected connected upstream | Hosting computer's network |
| Remote access | Not automatically published | Optional bindings through private tunnel and port access only |

Existing remote Squid/Tinyproxy services remain usable as client upstreams.
Hosting a new proxy does not grant additional network reachability: destinations
must already be reachable from the Desktop host. See [remote management and
hosting](REMOTE_HOSTING.md) for setup and [the security model](SECURITY.md) for
destination, identity and lifecycle boundaries.

HTTP forwarding uses Titanium.Web.Proxy without TLS interception or OS proxy
integration. The published CONNECT path is raw TCP, including non-TLS and
server-first protocols; the SOCKS5 path permits TCP CONNECT only. Published-proxy
policy checks DNS results and pins an allowed destination address. Protocol and
SDK adapters have local integration tests; these do not replace authorized live
relay and native-platform validation before a release.

Three port concepts remain separate: the service's local listening port, allowed
destination ports, and the published remote tunnel port. General destination
ports apply to all proxy requests. CONNECT ports add a restriction for HTTP
CONNECT and SOCKS5 TCP CONNECT. Empty port filters preserve the legacy unrestricted
port selection within an otherwise matching destination rule. Lists and inclusive
ranges are supported. The first matching enabled rule decides; an explicit deny
cannot be bypassed by a later allow or by the allow-all fallback. Private and
loopback access still require a matching explicit allow rule. Metadata/link-local
addresses remain blocked even when a broad allow rule is present.

These are the access-policy controls relevant to the Hub use case, not support
for importing arbitrary squid.conf, Squid caching, SSL Bump or external helpers.
