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
- Keep selective route-proxy listeners on loopback and never expose their local
  port through a Dev Tunnel.
- Treat real route destination lists and upstream proxy addresses as local
  configuration; do not publish private network topology in repository files.

## Dependency and image controls

- Dependencies are locked and installed with lifecycle scripts disabled. The
  Microsoft connections SDK currently requires an explicit `uuid` 11.1.1 override
  to fix [CVE-2026-41907](https://github.com/advisories/GHSA-w5hq-g745-h8pq).
  Version 11 retains the CommonJS `v4()` API used by the SDK; mapping tests cover it.
- Images use digest-pinned Debian 13 bases, refreshed distribution packages, and
  the Hub uses Node 24 LTS. Dev Tunnels downloads require checksum and version
  verification. Package metadata for the bundled .NET CLI remains in the image
  for vulnerability scanners.
- GitHub Actions are pinned to reviewed upstream commits. Dependabot covers
  workflows, every Dockerfile and the Hub npm lockfile.
- The reusable `Security gate` workflow runs static code analysis and scans
  source configuration, redacts
  secret findings in Git history, audits runtime and development npm packages,
  and scans changed images for both supported architectures before publication.
  Version releases and manual runs scan the complete image suite. Image gates include unpatched HIGH/CRITICAL
  findings; no `--ignore-unfixed` exception or CVE baseline is applied.

An updated base image does **not** imply that all vendor vulnerabilities have
patches. Failed security gates block publication until findings are fixed or an
explicit, evidence-backed policy decision is reviewed. Scanner severity, vendor
status and application reachability are different facts; never silently mark an
unpatched vendor advisory as fixed.

For local reproduction on Linux x86_64, use a dedicated tool directory outside
the repository:

```bash
security_tools="$(mktemp -d)"
bash .github/scripts/install-security-tools.sh "$security_tools"
npm --prefix images/hub audit --audit-level=low
"$security_tools/gitleaks" git --redact --no-banner .
"$security_tools/trivy" fs --scanners misconfig --severity HIGH,CRITICAL \
  --exit-code 1 --skip-dirs .git --skip-dirs images/hub/node_modules \
  --skip-dirs images/hub/dist .
docker build --pull -t devtunnel-toolkit-hub:security-local images/hub
"$security_tools/trivy" image --scanners vuln --severity HIGH,CRITICAL \
  --exit-code 1 devtunnel-toolkit-hub:security-local
```

Keep raw reports and environment-specific reproduction details outside the
repository. A clean scanner run is a point-in-time check, not a guarantee of the
absence of vulnerabilities.

## OpenVPN privilege limitation

The standalone OpenVPN image needs root for TUN/iptables initialization and then
drops the daemon to `nobody`, retaining `CAP_NET_ADMIN` for network interface
operations. Compose isolates networking on a bridge, publishes loopback by
default, drops all capabilities except `NET_ADMIN`, `SETUID` and `SETGID` at
startup, and enables `no-new-privileges`. It does not use `privileged: true`
or host networking. Namespace-local forwarding is configured by the runtime,
not by changing host sysctls from the entrypoint.

The image is not suitable for a deployment policy that
forbids root container startup, and the source configuration gate reports this
HIGH finding without suppressing it. Redesign initialization before publishing
under that policy. The Hub, Squid and route-proxy images run as non-root.

Run `OPENVPN_TEST_IMAGE=<local-image> node .github/scripts/openvpn-smoke.mjs`
to test TCP/UDP client-server traffic to a synthetic private HTTP service,
loopback publication, capability reduction and restart/PKI persistence.
This Docker integration test needs `/dev/net/tun`; it does not test real
destinations or forwarding through the Microsoft relay.

OpenVPN credentials and exported profiles are created with a private umask.
Client names cannot escape their PKI directory. An incomplete existing PKI is
never deleted or replaced automatically; recover it explicitly before retrying.
