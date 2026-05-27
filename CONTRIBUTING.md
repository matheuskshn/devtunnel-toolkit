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
```

## Pull requests

- Keep changes focused and small.
- Update `README.md` when behavior or usage changes.
- Run the relevant Docker build or wrapper checks before opening a PR.
- Do not commit secrets, access tokens, or local tunnel credentials.

## Releases

Releases are created from Git tags:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The Docker workflow publishes versioned images when a SemVer tag is pushed.
