# Contributing

Thanks for helping improve `devtunnel-toolkit`.

## Development

Build the image locally:

```bash
make build
```

Run the wrapper help:

```bash
make help
```

Validate the Docker Compose file:

```bash
docker compose config
docker compose \
  --env-file .env.route-proxy.example \
  -f compose.route-proxy.yml \
  config
```

Build the optional client-side route proxy:

```bash
make route-proxy-build
```

## Pull requests

- Keep changes focused and small.
- Update `README.md` when behavior or usage changes.
- Run the relevant Docker build or wrapper checks before opening a PR.
- Do not commit secrets, access tokens, or local tunnel credentials.
- Do not commit real internal hostnames, proxy addresses, or private network
  topology in route-proxy examples; use reserved `.test` names and local
  ignored environment files.

## Releases

Releases are created from Git tags:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The Docker workflow publishes versioned images when a SemVer tag is pushed.
