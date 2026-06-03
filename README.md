# DevTunnel Toolkit

[![Docker](https://github.com/matheuskshn/devtunnel-toolkit/actions/workflows/docker.yml/badge.svg)](https://github.com/matheuskshn/devtunnel-toolkit/actions/workflows/docker.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![GHCR](https://img.shields.io/badge/GHCR-devtunnel--toolkit-24292f?logo=github)](https://github.com/matheuskshn/devtunnel-toolkit/pkgs/container/devtunnel-toolkit)
[![Docker Hub](https://img.shields.io/badge/Docker%20Hub-devtunnel--toolkit-2496ed?logo=docker&logoColor=white)](https://hub.docker.com/r/matheuskshn/devtunnel-toolkit)

Developer-focused toolkit that extends Microsoft Dev Tunnels with local network
access helpers.

It packages three services:

| Service | Image | Purpose |
| --- | --- | --- |
| DevTunnel | `devtunnel-toolkit` | Hosts or connects Microsoft Dev Tunnels |
| Squid | `devtunnel-toolkit-squid` | HTTP/HTTPS proxy for local network access |
| OpenVPN | `devtunnel-toolkit-openvpn` | Privileged TCP VPN server for routed local network access |

## Why

Dev Tunnels is great for exposing a local development service. This toolkit adds
two common development workflows:

- Use a proxy through the tunnel to reach internal HTTP/HTTPS/SSH-over-CONNECT endpoints.
- Use a VPN through the tunnel to route development traffic to the network where the tunnel host is running.

The default proxy port is `3140`, matching the Squid configuration used by the
`rhel_squid_proxy_install` Ansible role.

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

The Squid defaults are based on the Ansible role:

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
| `DEVTUNNEL_DNS_PRIMARY` | `1.1.1.1` | First DNS server used by the DevTunnel container |
| `DEVTUNNEL_DNS_SECONDARY` | `8.8.8.8` | Second DNS server used by the DevTunnel container |
| `DEVTUNNEL_DNS_FALLBACK` | `127.0.0.1` | Fallback to the host resolver when external DNS is unavailable |

The DevTunnel container uses external DNS by default because some local
resolvers do not resolve Microsoft Dev Tunnels service domains. To disable the
external DNS override and use only the system resolver, set:

```bash
COMPOSE_FILE=compose.yml:compose.system-dns.yml
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
- `ALLOW_ANONYMOUS=true` can expose your proxy/VPN to anyone with the tunnel URL or connection details.
- OpenVPN runs as a privileged container because it needs `/dev/net/tun`, IP forwarding, and NAT rules.
- Treat generated `.ovpn` files, tunnel URLs, and access tokens as secrets.
- Review pushed VPN routes before starting the service in sensitive networks.

## Development

```bash
make build
make help
docker compose config
```

## References

- Microsoft Dev Tunnels quickstart: https://learn.microsoft.com/azure/developer/dev-tunnels/get-started
- Microsoft Dev Tunnels CLI reference: https://learn.microsoft.com/azure/developer/dev-tunnels/cli-commands
- Squid project: https://www.squid-cache.org/
- OpenVPN project: https://openvpn.net/
