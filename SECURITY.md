# Security Policy

## Reporting a vulnerability

Please do not open a public issue for vulnerabilities.

Report security concerns privately through GitHub Security Advisories if they
are enabled for the repository. If advisories are not available, contact the
repository owner through GitHub.

## Scope

This project packages the Microsoft `devtunnel` CLI in a Docker image. Issues
in the upstream CLI should also be reported to Microsoft through the official
Dev Tunnels issue tracker.

## Operational guidance

- Treat tunnel URLs and access tokens as secrets.
- Avoid `ALLOW_ANONYMOUS=true` unless the exposed service is safe for public access.
- Review what is listening on the exported port before starting a tunnel.
