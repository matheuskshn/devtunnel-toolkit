# DevTunnel Toolkit

[![Docker](https://github.com/matheuskshn/devtunnel-toolkit/actions/workflows/docker.yml/badge.svg)](https://github.com/matheuskshn/devtunnel-toolkit/actions/workflows/docker.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![GHCR](https://img.shields.io/badge/GHCR-devtunnel--toolkit-24292f?logo=github)](https://github.com/matheuskshn/devtunnel-toolkit/pkgs/container/devtunnel-toolkit)
[![Docker Hub](https://img.shields.io/badge/Docker%20Hub-devtunnel--toolkit-2496ed?logo=docker&logoColor=white)](https://hub.docker.com/r/matheuskshn/devtunnel-toolkit)

Developer-focused toolkit that extends Microsoft Dev Tunnels with local network
access helpers.

It packages three server-side services and one optional client-side helper:

| Service | Image | Purpose |
| --- | --- | --- |
| DevTunnel | `devtunnel-toolkit` | Hosts or connects Microsoft Dev Tunnels |
| Squid | `devtunnel-toolkit-squid` | HTTP/HTTPS proxy for local network access |
| OpenVPN | `devtunnel-toolkit-openvpn` | Privileged TCP VPN server for routed local network access |
| Route proxy | `devtunnel-toolkit-route-proxy` | Selectively sends configured destinations through a locally connected tunnel proxy |

## Why

Dev Tunnels is great for exposing a local development service. This toolkit adds
two common development workflows:

- Use a proxy through the tunnel to reach internal HTTP/HTTPS/SSH-over-CONNECT endpoints.
- Use a VPN through the tunnel to route development traffic to the network where the tunnel host is running.
- Optionally keep normal client traffic direct while routing only selected
  destinations through the tunneled proxy.

## Quickstart

Login once:

```bash
make login
```

Or login with GitHub:

```bash
make login-github
```

Confirm the container volume has a cached login:

```bash
make status
```

Start the toolkit attached to the logs:

```bash
make up
```

When running attached, stopping the command with Ctrl+C also stops the Compose
services. To start in the background, use:

```bash
make up-d
```

By default, the tunnel hosts:

| Port | Service |
| --- | --- |
| `3140` | Squid proxy |
| `53194` | OpenVPN TCP server |

Use a persistent tunnel ID when desired:

```bash
TUNNEL_ID=my-dev-gateway make up
```

Allow anonymous access only when you understand the exposure:

```bash
ALLOW_ANONYMOUS=true TUNNEL_ID=my-dev-gateway make up
```

## Docker Compose

Login once with Microsoft/Entra ID:

```bash
docker compose run --rm devtunnel login microsoft
```

Or login with GitHub:

```bash
docker compose run --rm devtunnel login github
```

Confirm the login before starting the long-running services:

```bash
docker compose run --rm devtunnel auth-check
```

Start everything in the background:

```bash
docker compose up -d
```

Or with Make:

```bash
make up-d
```

This starts:

| Service | Local endpoint |
| --- | --- |
| Squid proxy | `127.0.0.1:3140` |
| OpenVPN server | `127.0.0.1:53194/tcp` |
| DevTunnel host | ports `3140,53194` |

Follow the tunnel logs:

```bash
docker compose logs -f devtunnel
```

Use a persistent tunnel ID:

```bash
TUNNEL_ID=my-dev-gateway docker compose up -d
```

Expose only the proxy:

```bash
PORTS=3140 docker compose up -d squid devtunnel
```

Expose only the VPN:

```bash
PORTS=53194 docker compose up -d openvpn devtunnel
```

Generate an OpenVPN client profile:

```bash
docker compose run --rm openvpn client > devtunnel-toolkit.ovpn
```

Stop the toolkit:

```bash
docker compose down
```

Delete containers, networks, and toolkit volumes:

```bash
docker compose down --volumes --remove-orphans
```

This removes the cached DevTunnel login and OpenVPN PKI/client certificates.
After this command, run login again and regenerate any `.ovpn` files.

Equivalent Make targets:

```bash
make reset
make recreate
```

`make reset` removes containers, networks, and volumes. `make recreate` removes
and recreates containers while keeping volumes, so the cached DevTunnel login
and OpenVPN certificates are preserved. For a full wipe, run `make reset`, then
login again and start with `make up`.

## Authentication

Dev Tunnels requires login to create and host tunnels. The CLI supports
Microsoft/Entra ID and GitHub accounts; after login, Microsoft documents that
the token is cached in the system secure key chain for several days in the
[Dev tunnels CLI reference](https://learn.microsoft.com/azure/developer/dev-tunnels/cli-commands#manage-user-credentials).

This project mounts `/home/devtunnel` as a named Docker volume so normal
`docker compose stop`, `docker compose restart`, `docker compose down`, and
`make up` runs can reuse the cached login. Commands that remove volumes, such as
`make reset` or `docker compose down --volumes`, remove that cache.

`make up` runs an auth check before starting the tunnel host. If it says
`Not logged in`, run:

```bash
make login-github
make status
make up
```

`docker compose up` also starts `devtunnel-renew`, a lightweight sidecar that
shares the DevTunnel home volume. For GitHub logins, it reads the cached access
token expiration and runs a DevTunnel management check shortly after expiration
so the CLI can refresh and rotate the cached GitHub token pair before an idle
host has to reconnect with old credentials. It does not make credentials
permanent; if GitHub or Microsoft revoke a token, or if the refresh token
expires, run the login flow again.

The host container also watches the `devtunnel host` output for Unauthorized
session-refresh errors. When that happens, it exits with a non-zero code so
Docker's `restart: unless-stopped` policy creates a fresh host session using
the current token cache.

Run one renew probe manually with:

```bash
make renew
```

For unattended runs, provide an access token instead of a cached interactive
login:

```bash
TUNNEL_ID=my-dev-gateway \
DEVTUNNEL_ACCESS_TOKEN=... \
docker compose up -d
```

The token is passed to `devtunnel host/connect --access-token`. Treat it as a
secret; environment variables can be visible through Docker inspection and shell
history.

## Client workflow

On the client machine, run the DevTunnel client:

```bash
docker run --rm -it \
  -v devtunnel-home:/home/devtunnel \
  ghcr.io/matheuskshn/devtunnel-toolkit:edge login microsoft

docker run --rm -it \
  -v devtunnel-home:/home/devtunnel \
  ghcr.io/matheuskshn/devtunnel-toolkit:edge connect my-dev-gateway
```

After `connect`, the tunnel ports are available on the client as local ports.

### Use the proxy

Configure a terminal or tool to use:

```bash
export http_proxy=http://127.0.0.1:3140
export https_proxy=http://127.0.0.1:3140
export no_proxy=localhost,127.0.0.1
```

Then use internal HTTP/HTTPS endpoints normally.

This process-wide configuration sends every HTTP/HTTPS request made by the
configured application to the tunneled Squid proxy. If an application should
use the tunnel for only a small set of destinations, use the optional selective
local proxy below.

### Selective local proxy

The route proxy is a client-side complement to the server-side Squid proxy; it
does not replace Squid. Start it on the machine where `devtunnel connect` has
already made the remote Squid port available as `127.0.0.1:3140`.

The route proxy listens on `127.0.0.1:8888`. Requests for configured destination
hostnames use the local DevTunnel endpoint as an upstream HTTP proxy. Requests
for all other destinations are connected directly from the client machine.

```text
application
  -> route proxy at 127.0.0.1:8888
       -> configured destinations: DevTunnel/Squid at 127.0.0.1:3140
       -> all other destinations: direct connection
```

Set one or more comma-separated destination hostnames. Use reserved example
names in shared documentation; keep actual internal hostnames in an untracked
local environment file.

For a quick one-off start, provide the destinations directly through
`ROUTE_PROXY_HOSTS`:

```bash
ROUTE_PROXY_HOSTS=api.internal.example.test,.services.internal.example.test \
  make route-proxy-up
```

Multiple destinations are separated by commas. An inline value takes precedence
over the optional local environment file described below.

For repeatable local use, copy the dedicated public template:

```bash
cp .env.route-proxy.example .env.route-proxy
```

Edit only `.env.route-proxy` with the real local destinations and upstream
address. The generated file is ignored by Git. The route-proxy Make targets
automatically load it when present:

```bash
make route-proxy-up
make route-proxy-logs
```

Then scope the proxy environment to the application that needs it:

```bash
HTTP_PROXY=http://127.0.0.1:8888 \
HTTPS_PROXY=http://127.0.0.1:8888 \
NO_PROXY=localhost,127.0.0.1 \
your-command
```

The equivalent Docker Compose command is:

```bash
docker compose \
  --env-file .env.route-proxy \
  -f compose.route-proxy.yml \
  up -d --build
```

Stop the local helper without affecting the server-side Toolkit stack:

```bash
make route-proxy-down
```

A hostname such as `api.internal.example.test` matches only that exact host. A
leading dot, such as `.services.internal.example.test`, matches hosts in that
domain according to Tinyproxy's upstream matching rules. The route list is a
routing decision, not an authorization boundary; the remote Squid and target
network remain responsible for access control.

The route proxy uses HTTP `CONNECT` for HTTPS and does not terminate, decrypt,
or bypass TLS. Application traffic remains protected by the destination's TLS
certificate validation. Its default log level records warnings and errors, not
successful request URLs.

The Squid service uses these defaults:

| Setting | Default |
| --- | --- |
| Listen address | `127.0.0.1` |
| HTTP port | `3140` |
| SSL ports | `443 563 22` |
| Safe ports | `80 21 22 443 70 210 1025-65535 280 488 591 777` |

### Use the VPN

Generate a client profile on the server side:

```bash
make ovpn-client > devtunnel-toolkit.ovpn
```

The generated profile defaults to:

```text
remote 127.0.0.1 53194
proto tcp-client
```

That means the OpenVPN client connects to the local forwarded port created by
`devtunnel connect`.

## Configuration

### DevTunnel

| Variable | Default | Description |
| --- | --- | --- |
| `PORTS` | `3140,53194` | Comma-separated ports hosted by Dev Tunnels |
| `TUNNEL_ID` | empty | Existing tunnel ID to host or connect |
| `DEVTUNNEL_ACCESS_TOKEN` | empty | Optional token passed as `--access-token` to `host` and `connect` |
| `ALLOW_ANONYMOUS` | `false` | Adds `--allow-anonymous` when true |
| `PROTOCOL` | empty | Optional `http`, `https`, or `auto` |
| `EXPIRATION` | empty | Optional tunnel expiration, such as `2h` or `7d` |
| `VERBOSE` | `false` | Adds `--verbose` when true |
| `LOGIN_PROVIDER` | `microsoft` | Provider used by bare `login` |
| `DEVTUNNEL_EXIT_ON_UNAUTHORIZED` | `true` | Exit the host container when DevTunnel reports an Unauthorized host-session refresh |
| `DEVTUNNEL_UNAUTHORIZED_EXIT_CODE` | `75` | Exit code used when the Unauthorized host-session guard trips |
| `DEVTUNNEL_EXIT_TERMINATION_GRACE_SECONDS` | `10` | Seconds to wait before force-killing a stuck host process after the guard trips |
| `DEVTUNNEL_RENEW_AFTER_EXPIRATION_SECONDS` | `60` | Seconds after cached GitHub access-token expiration before `devtunnel-renew` probes the tunnel |
| `DEVTUNNEL_RENEW_FALLBACK_INTERVAL_SECONDS` | `29700` | Fallback renew interval when GitHub token expiration cannot be read |
| `DEVTUNNEL_RENEW_RETRY_SECONDS` | `300` | Retry delay after a failed renew probe |
| `DEVTUNNEL_RENEW_MIN_SLEEP_SECONDS` | `30` | Minimum sleep when the next renew time is very close |
| `DEVTUNNEL_DNS_PRIMARY` | `1.1.1.1` | First DNS server used by the DevTunnel container |
| `DEVTUNNEL_DNS_SECONDARY` | `8.8.8.8` | Second DNS server used by the DevTunnel container |
| `DEVTUNNEL_DNS_FALLBACK` | `127.0.0.1` | Fallback to the host resolver when external DNS is unavailable |

The DevTunnel container uses external DNS by default because some local
resolvers do not resolve Microsoft Dev Tunnels service domains. To disable the
external DNS override and use only the system resolver, set:

```bash
COMPOSE_FILE=compose.yml:compose.system-dns.yml
```

### CA certificates

The host CA bundle is mounted read-only into the DevTunnel, Squid, and OpenVPN
containers so internal TLS endpoints can use locally trusted corporate CAs.

| Variable | Default | Description |
| --- | --- | --- |
| `LOCAL_CA_BUNDLE` | `/etc/ssl/certs/ca-certificates.crt` | Host CA bundle path; common on Debian/Ubuntu hosts |
| `CONTAINER_CA_BUNDLE` | `/etc/ssl/certs/ca-certificates.crt` | CA bundle path inside the Debian-based toolkit images |

On RHEL, Rocky, Fedora, and similar hosts, set:

```bash
LOCAL_CA_BUNDLE=/etc/pki/tls/certs/ca-bundle.crt
```

### Squid

| Variable | Default | Description |
| --- | --- | --- |
| `SQUID_HTTP_PORT` | `3140` | Squid listen port inside the container |
| `SQUID_LISTEN_ADDRESS` | `127.0.0.1` | Squid listen address inside the container |
| `SQUID_VISIBLE_HOSTNAME` | `devtunnel-toolkit-squid` | Squid visible hostname |
| `SQUID_SSL_PORTS` | `443 563 22` | Ports allowed for CONNECT |
| `SQUID_SAFE_PORTS` | `80 21 22 443 70 210 1025-65535 280 488 591 777` | Safe destination ports |
| `SQUID_EXTRA_CONFIG` | empty | Extra raw Squid configuration lines |

### Selective route proxy

The route proxy is intentionally defined in the separate
`compose.route-proxy.yml` file because it runs on the client machine, after the
DevTunnel connection is established. It is not started by the default
server-side Compose stack.

| Variable | Default | Description |
| --- | --- | --- |
| `ROUTE_PROXY_ENV_FILE` | `.env.route-proxy` | Local file automatically loaded by the Make targets when it exists |
| `ROUTE_PROXY_IMAGE` | `devtunnel-toolkit-route-proxy:local` | Route proxy image used by the client-side Compose file |
| `ROUTE_PROXY_LISTEN_ADDRESS` | `127.0.0.1` | Local listener; only IPv4 or IPv6 loopback is accepted |
| `ROUTE_PROXY_PORT` | `8888` | Local application-facing proxy port |
| `ROUTE_PROXY_UPSTREAM_HOST` | `127.0.0.1` | Host where `devtunnel connect` exposes the remote proxy |
| `ROUTE_PROXY_UPSTREAM_PORT` | `3140` | Port where `devtunnel connect` exposes the remote proxy |
| `ROUTE_PROXY_HOSTS` | empty, required | Comma-separated exact hostnames or leading-dot domain patterns routed through the upstream proxy |
| `ROUTE_PROXY_CONNECT_PORTS` | `443` | Space-separated destination ports allowed for HTTP `CONNECT` |

The container fails closed when `ROUTE_PROXY_HOSTS` is empty or malformed. It
also rejects non-loopback listener addresses so this local helper cannot be
accidentally published on the network. Do not put URL schemes, paths, query
strings, credentials, or ports in `ROUTE_PROXY_HOSTS`.

### OpenVPN

| Variable | Default | Description |
| --- | --- | --- |
| `OVPN_PORT` | `53194` | OpenVPN listen port inside the container |
| `OVPN_LISTEN_ADDRESS` | `127.0.0.1` | OpenVPN listen address |
| `OVPN_PROTO` | `tcp` | `tcp` or `udp`; TCP is recommended for Dev Tunnels |
| `OVPN_NETWORK` | `10.8.0.0` | VPN subnet network |
| `OVPN_NETMASK` | `255.255.255.0` | VPN subnet mask |
| `OVPN_CIDR` | `10.8.0.0/24` | VPN subnet CIDR used for NAT |
| `OVPN_CLIENT_NAME` | `devtunnel-toolkit` | Default client certificate/profile name |
| `OVPN_REMOTE_HOST` | `127.0.0.1` | Remote host written to generated client profiles |
| `OVPN_REMOTE_PORT` | `53194` | Remote port written to generated client profiles |
| `OVPN_PUSH_ROUTES` | RFC1918 routes | Comma-separated routes pushed to clients |
| `OVPN_DNS` | empty | Comma-separated DNS servers pushed to clients |
| `OVPN_REDIRECT_GATEWAY` | `false` | Push default route when true |
| `OVPN_EXTRA_CONFIG` | empty | Extra raw OpenVPN server configuration |

## Published images

```bash
docker pull ghcr.io/matheuskshn/devtunnel-toolkit:edge
docker pull ghcr.io/matheuskshn/devtunnel-toolkit-squid:edge
docker pull ghcr.io/matheuskshn/devtunnel-toolkit-route-proxy:edge
docker pull ghcr.io/matheuskshn/devtunnel-toolkit-openvpn:edge
```

Docker Hub publishing uses the same image names under `matheuskshn/` when the
repository secrets are configured.

## Versioning

Images follow Git tags.

| Git event | Published tags |
| --- | --- |
| Pull request | Build only, no push |
| Push to `main` | `edge`, `main` |
| Tag `v1.2.3` | `v1.2.3`, `1.2.3`, `1.2`, `1`, `latest` |

Release a new version:

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Security notes

- Squid and OpenVPN are bound to `127.0.0.1` on the host; Dev Tunnels exposes those local ports.
- The selective route proxy is bound to client loopback and must not be exposed
  through Dev Tunnels or published on a non-loopback interface.
- Destination lists can reveal internal naming conventions. Keep real values in
  ignored local environment files and use reserved `.test` examples in commits,
  issues, logs, and documentation.
- The selective route list controls forwarding only. It is not a firewall or an
  authorization policy.
- `ALLOW_ANONYMOUS=true` can expose your proxy/VPN to anyone with the tunnel URL or connection details.
- OpenVPN runs as a privileged container because it needs `/dev/net/tun`, IP forwarding, and NAT rules.
- Treat generated `.ovpn` files, tunnel URLs, and access tokens as secrets.
- Review pushed VPN routes before starting the service in sensitive networks.

## Development

```bash
make build
make help
docker compose config
ROUTE_PROXY_HOSTS=api.internal.example.test \
  docker compose -f compose.route-proxy.yml config
make route-proxy-build
```

## References

- Microsoft Dev Tunnels quickstart: https://learn.microsoft.com/azure/developer/dev-tunnels/get-started
- Microsoft Dev Tunnels CLI reference: https://learn.microsoft.com/azure/developer/dev-tunnels/cli-commands
- Squid project: https://www.squid-cache.org/
- Tinyproxy project: https://tinyproxy.github.io/
- OpenVPN project: https://openvpn.net/
