# Image workflows

`docker.yml` builds the toolkit, Squid, route proxy and OpenVPN images.
`hub.yml` validates and publishes Hub development snapshots separately. Both call
`image-changes.yml`, which selects images using repository-local code with
read-only repository permissions and no registry credentials.

## Build inputs

| Image | Inputs that select a build |
| --- | --- |
| `devtunnel-toolkit` | Root `Dockerfile`, root `.dockerignore`, `docker/devtunnel-entrypoint` |
| `devtunnel-toolkit-squid` | `images/squid/`: Dockerfile, `.dockerignore`, entrypoint and configuration template |
| `devtunnel-toolkit-route-proxy` | `images/tinyproxy/`: Dockerfile, `.dockerignore`, entrypoint and configuration template |
| `devtunnel-toolkit-openvpn` | `images/openvpn/`: Dockerfile, `.dockerignore` and entrypoint |
| `devtunnel-toolkit-hub` | `images/hub/`: Dockerfile, `.dockerignore`, package manifests, `tsconfig.json`, `src/`, `web/` and `bin/` |

Update `scripts/image-changes.mjs` and its tests when adding a new build input,
such as a file newly referenced by Dockerfile `COPY`. Changes to an image's
build definition affect that image only. Workflow, documentation, example and
test changes do not select image builds on ordinary pushes or PRs. Small selection/validation jobs can
still run; an empty selection skips the image jobs, including registry login.

## Comparison rules

- Push: compare the event's before/after commits, including all commits in a batch.
- Pull request: compare the PR head against its merge base with the target branch.
- Version tag: the suite coordinator handles all five images, even without code changes.
- Manual execution: explicitly rebuild every image managed by the chosen workflow,
  even without code changes. The Hub workflow builds only the Hub; Docker builds
  the four other images. These runs publish unique development snapshots, never
  exact release versions. Only `main` runs update `edge` and `main`.
- First commit or newly created branch: inspect all current build inputs.

Deleted and renamed inputs also select affected images. Comparison errors fail
the job instead of silently treating unknown state as unchanged. The selector
compares Git content, not the last successful publication or changes in upstream
base images/packages. It does not rebuild for upstream updates alone.

Use a manual execution to rebuild for upstream base-image or package updates.
Ordinary pushes leave unchanged images and their published tags untouched.

## Local checks

```sh
node .github/scripts/release-policy.mjs
node --test .github/scripts/*.test.mjs
cd images/hub
npm test
npm run check:public
```

Hub publication requires both local container tests and multi-architecture builds
to succeed. Pull requests never publish Hub images. Docker Hub publication needs
repository secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`; optional variable
`DOCKERHUB_NAMESPACE` overrides the username as image namespace. GHCR uses the
workflow token. Keep all credential values out of source and examples.

Suite releases use `release-please.yml`, `release.yml`, `release-suite.yml` and
`release-checks.yml`. See [the release guide](../RELEASING.md) for versioning,
draft releases, immutable-tag checks, registry promotion and retry behavior.

## Local coverage and analysis

Run `npm ci --ignore-scripts` and `npm run test:coverage` in `images/hub`.
The development coverage tool requires Node 22.12+; CI uses Node 24 LTS.
The command runs the Hub and release automation tests, includes unexecuted
application files, and writes `images/hub/coverage/lcov.info`. TypeScript source
maps relate executed JavaScript to its original source; coverage files are not
included in the published images. Container integration tests remain separate
and must also pass before release.

`sonar-project.properties` provides the shared scanner scope and LCOV path.
Supply `SONAR_HOST_URL` and `SONAR_TOKEN` outside the repository. Set the project
version and SCM revision explicitly. For a before/after review, analyze a clean
archive of the selected Git commit with its matching Git metadata first, then use that successful analysis as
the new-code baseline for the working copy. Keep exactly the same source scope
for both scans; changing inclusions is not a code regression. A working-copy
analysis is not a published commit or release.

The coverage runner refuses stale compiled JavaScript with no matching current
TypeScript source, without deleting local artifacts. Use a clean build directory
or review/remove only the reported obsolete outputs before retrying.

Sonar is not automatically connected to a remote account by this configuration.
The scanner waits for its quality gate; unavailable coverage or failed gates
must not be silently converted to successful publication checks.

Git, Docker and Skopeo used by release automation must be system-managed Unix
executables in `/usr/bin`, `/usr/local/bin` or `/bin`. The resolved executable
and every parent directory must be root-owned and not group/world writable.
The automation intentionally ignores inherited PATH entries for these tools.
User-writable shims are not supported; install trusted tools system-wide.
