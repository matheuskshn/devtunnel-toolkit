# Changelog

## [0.1.0-rc.8](https://github.com/matheuskshn/devtunnel-toolkit/compare/v0.1.0-rc.7...v0.1.0-rc.8) (2026-09-20)


### Bug Fixes

* update base Dev Tunnel CLI checksum ([#33](https://github.com/matheuskshn/devtunnel-toolkit/issues/33)) ([12b86bf](https://github.com/matheuskshn/devtunnel-toolkit/commit/12b86bf602996df5b0f6144dde65b23a5e4ce673))

## [0.1.0-rc.7](https://github.com/matheuskshn/devtunnel-toolkit/compare/v0.1.0-rc.6...v0.1.0-rc.7) (2026-09-20)


### Features

* **hub:** manage tunnel authentication validity ([#31](https://github.com/matheuskshn/devtunnel-toolkit/issues/31)) ([3a51a68](https://github.com/matheuskshn/devtunnel-toolkit/commit/3a51a6898d488b8a07e9d3a10bd27704bf4180f8))

## [0.1.0-rc.6](https://github.com/matheuskshn/devtunnel-toolkit/compare/v0.1.0-rc.5...v0.1.0-rc.6) (2026-09-16)


### Features

* **hub:** add assisted setup and infrastructure management ([#23](https://github.com/matheuskshn/devtunnel-toolkit/issues/23)) ([3a88e4a](https://github.com/matheuskshn/devtunnel-toolkit/commit/3a88e4a00a3c535dffb0e3df92ad568e81129756))

## [0.1.0-rc.5](https://github.com/matheuskshn/devtunnel-toolkit/compare/v0.1.0-rc.4...v0.1.0-rc.5) (2026-09-12)


### Features

* **hub:** add secure console and native Ubuntu acceptance ([#16](https://github.com/matheuskshn/devtunnel-toolkit/issues/16)) ([91c4706](https://github.com/matheuskshn/devtunnel-toolkit/commit/91c470615e9f33ca261629dd6cf18ac4ae2b0063))


### Bug Fixes

* **ci:** install dependencies before release tests ([#21](https://github.com/matheuskshn/devtunnel-toolkit/issues/21)) ([447f24a](https://github.com/matheuskshn/devtunnel-toolkit/commit/447f24ab87beab2b59b00cb7cf3cdb822105b200))

## [0.1.0-rc.4](https://github.com/matheuskshn/devtunnel-toolkit/compare/v0.1.0-rc.3...v0.1.0-rc.4) (2026-09-09)


### Features

* **hub:** derive fixed tunnel names from verified logins ([#10](https://github.com/matheuskshn/devtunnel-toolkit/issues/10)) ([88f7c48](https://github.com/matheuskshn/devtunnel-toolkit/commit/88f7c48e0d983505333f3c78d872c5f1fd1eaa6c))

## [0.1.0-rc.3](https://github.com/matheuskshn/devtunnel-toolkit/compare/v0.1.0-rc.2...v0.1.0-rc.3) (2026-09-09)


### Features

* **hub:** add explicit all-domain access policy ([#8](https://github.com/matheuskshn/devtunnel-toolkit/issues/8)) ([c6e45ad](https://github.com/matheuskshn/devtunnel-toolkit/commit/c6e45adc5f3dbf29b455bc869410eacf31b23fe8))

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
