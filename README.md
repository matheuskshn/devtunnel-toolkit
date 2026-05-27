# devtunnel-container

[![Docker](https://github.com/matheuskshn/devtunnel-container/actions/workflows/docker.yml/badge.svg)](https://github.com/matheuskshn/devtunnel-container/actions/workflows/docker.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![GHCR](https://img.shields.io/badge/GHCR-devtunnel--container-24292f?logo=github)](https://github.com/matheuskshn/devtunnel-container/pkgs/container/devtunnel-container)
[![Docker Hub](https://img.shields.io/badge/Docker%20Hub-devtunnel--container-2496ed?logo=docker&logoColor=white)](https://hub.docker.com/r/matheuskshn/devtunnel-container)

Docker image for the Microsoft `devtunnel` CLI, with a small helper entrypoint
for day-to-day usage.

It is designed for one simple job: expose a local port through Dev Tunnels
without installing the CLI on the host.

## Highlights

| Feature | Status |
| --- | --- |
| Microsoft/Entra ID login | `make login` or `login microsoft` |
| GitHub login | `make login-github` or `login github` |
| Default tunnel port | `3140` |
| Persistent auth cache | Docker volume at `/home/devtunnel` |
| Non-root container user | `devtunnel` |
| Multi-arch images | `linux/amd64`, `linux/arm64` |
| Registries | GitHub Container Registry and Docker Hub |

## Quickstart

Build locally:

```bash
make build
```

Login with Microsoft/Entra ID:

```bash
make login
```

Or login with GitHub:

```bash
make login-github
```

Expose the default port, `3140`:

```bash
make host
```

Expose another port:

```bash
PORTS=8080 make host
```

Expose multiple ports:

```bash
PORTS=3140,8080 make host
```

Allow anonymous access:

```bash
PORTS=3140 ALLOW_ANONYMOUS=true make host
```

Use an existing persistent tunnel:

```bash
TUNNEL_ID=my-tunnel PORTS=3140 make host
```

Check login status:

```bash
make status
```

## Published images

After the repository is published and the workflow runs, images are available
from both registries:

```bash
docker pull ghcr.io/matheuskshn/devtunnel-container:edge
docker pull docker.io/matheuskshn/devtunnel-container:edge
```

Run from GHCR:

```bash
docker run --rm -it \
  --network host \
  -v devtunnel-home:/home/devtunnel \
  ghcr.io/matheuskshn/devtunnel-container:edge login microsoft

docker run --rm -it \
  --network host \
  -v devtunnel-home:/home/devtunnel \
  -e PORTS=3140 \
  ghcr.io/matheuskshn/devtunnel-container:edge host
```

Run from Docker Hub:

```bash
docker run --rm -it \
  --network host \
  -v devtunnel-home:/home/devtunnel \
  docker.io/matheuskshn/devtunnel-container:edge login github
```

## Docker Compose

Login once:

```bash
docker compose run --rm devtunnel login microsoft
```

Or:

```bash
docker compose run --rm devtunnel login github
```

Start the tunnel:

```bash
PORTS=3140 docker compose up --build
```

## Commands

The image includes a helper wrapper:

| Command | Description |
| --- | --- |
| `login microsoft` | Login with Microsoft/Entra ID using device code auth |
| `login github` | Login with GitHub using device code auth |
| `status` | Show the current login state |
| `logout` | Clear the cached login |
| `host` | Host a tunnel using environment defaults |
| `connect <id>` | Connect to an existing tunnel |
| `raw <args>` | Run the native `devtunnel` CLI directly |

Any native CLI command can be executed with `raw`:

```bash
docker run --rm -it \
  --network host \
  -v devtunnel-home:/home/devtunnel \
  ghcr.io/matheuskshn/devtunnel-container:edge raw list
```

## Configuration

| Variable | Default | Description |
| --- | --- | --- |
| `PORTS` | `3140` | Comma-separated local ports to expose |
| `PORT` | empty | Fallback single-port value when `PORTS` is not set |
| `TUNNEL_ID` | empty | Existing tunnel ID to host or connect |
| `ALLOW_ANONYMOUS` | `false` | Adds `--allow-anonymous` when true |
| `PROTOCOL` | empty | Optional `http`, `https`, or `auto` |
| `EXPIRATION` | empty | Optional tunnel expiration, such as `2h` or `7d` |
| `VERBOSE` | `false` | Adds `--verbose` when true |
| `LOGIN_PROVIDER` | `microsoft` | Provider used by bare `login` |

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

The image version tracks this wrapper project. The Microsoft `devtunnel` binary
is downloaded during image build, so each published image contains the CLI
version available at build time.

## Publishing setup

GitHub Container Registry works with the repository `GITHUB_TOKEN`; the workflow
already requests `packages: write`.

For Docker Hub publishing, configure these GitHub repository secrets:

| Name | Required | Description |
| --- | --- | --- |
| `DOCKERHUB_USERNAME` | yes | Docker Hub username used for login |
| `DOCKERHUB_TOKEN` | yes | Docker Hub access token |

Optional repository variables:

| Name | Default | Description |
| --- | --- | --- |
| `DOCKERHUB_NAMESPACE` | `DOCKERHUB_USERNAME` | Docker Hub namespace/organization |
| `DOCKERHUB_REPOSITORY` | repository name | Docker Hub repository name |

## Security notes

- The default exported port is `3140`; check what is listening on that port before hosting a tunnel.
- Treat tunnel URLs and access tokens as secrets.
- Use `ALLOW_ANONYMOUS=true` only when the exposed service is safe for public access.
- The login cache is stored in the Docker volume mounted at `/home/devtunnel`.

## Development

```bash
make build
make help
docker compose config
```

## References

- Microsoft Dev Tunnels quickstart: https://learn.microsoft.com/azure/developer/dev-tunnels/get-started
- Microsoft Dev Tunnels CLI reference: https://learn.microsoft.com/azure/developer/dev-tunnels/cli-commands
- Docker GitHub Actions docs: https://docs.docker.com/build/ci/github-actions/
- GitHub Container Registry docs: https://docs.github.com/packages/guides/pushing-and-pulling-docker-images
