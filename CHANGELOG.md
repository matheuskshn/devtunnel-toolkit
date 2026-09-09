# Changelog

## [0.1.0-rc.2](https://github.com/matheuskshn/devtunnel-toolkit/compare/v0.1.0-rc.1...v0.1.0-rc.2) (2026-09-09)


### Features

* **hub:** add encrypted PostgreSQL session persistence ([#6](https://github.com/matheuskshn/devtunnel-toolkit/issues/6)) ([644482d](https://github.com/matheuskshn/devtunnel-toolkit/commit/644482dd658307381a961eaebad6e126ef36836d))

## 0.1.0-rc.1 (2026-09-08)


### Features

* **hub:** add multi-user proxy hub and coordinated suite releases ([#4](https://github.com/matheuskshn/devtunnel-toolkit/issues/4)) ([e736545](https://github.com/matheuskshn/devtunnel-toolkit/commit/e73654528ceab9a27c8bab21ebe248c16dbc4645))
* **infra:** add selective route proxy ([e2f6eef](https://github.com/matheuskshn/devtunnel-toolkit/commit/e2f6eef7b844db4e27e38911969a394d7b5a5a3d))

## Changelog

Release Please maintains this file from reviewed Conventional Commits.
Use component scopes: `toolkit`, `squid`, `route-proxy`, `openvpn`, and `hub`.

## Unreleased

### Hub

- Multi-user private tunnels with isolated login caches and a shared Squid proxy.
- Fixed tunnel names, server-side port mapping and session-owner audit attribution.
- Environment-variable configuration and portable container deployment examples.

### Delivery

- Selective development builds and coordinated, versioned suite releases.
