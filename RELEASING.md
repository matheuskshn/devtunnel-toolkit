# Versioning and releases

The toolkit uses one suite version for all five container images. `version.txt`
is the source of truth; the Hub package and lockfile must match it. The
Release Please manifest tracks proposed/released versions and is empty only
during bootstrap. The first proposed release is `0.1.0-rc.1`.

## Prepare a release

Use Conventional Commits with component scopes, for example:

```text
fix(hub): preserve session state during shutdown
feat(squid): support an additional proxy policy option
feat(toolkit)!: change an incompatible command-line argument
```

Scopes are `toolkit`, `squid`, `route-proxy`, `openvpn`, and `hub`; use `ci` for
delivery changes. Explain compatibility breaks with a `BREAKING CHANGE:` footer.
Review release notes as public product documentation, without deployment data,
real account details, private destinations, credentials or operational logs.

On `main`, **Release PR** uses Release Please to propose the version update,
Hub package/lockfile synchronization and changelog. It does not merge its PR.
The workflow validates the proposed commit directly and posts the
`Release version checks` status, so it does not depend on bot-created PR events
starting another workflow. Require that status in branch protection if desired.
Repository Settings > Actions > General must allow Actions to create pull
requests. The built-in `GITHUB_TOKEN` is used; no personal PAT is needed.

Merge the release PR after reviewing the version, changes and acceptance gates.
Release Please creates a draft release and calls the suite coordinator directly.
This does not depend on a tag push created with `GITHUB_TOKEN` triggering CI.
Human-pushed `vX.Y.Z` tags also enter the same coordinator and must match the
version files at the tagged commit. Only commits reachable from `origin/main`
are eligible for release.

## RC and stable versions

The initial configuration uses `prerelease: true`, `versioning: prerelease` and
`prerelease-type: rc`. It proposes RC updates until explicitly changed.
To graduate to stable, review a PR changing `prerelease` to `false` and
`versioning` to `default`, and use a Conventional Commit footer such as
`Release-As: 0.1.0` to request the first stable version. Let Release Please
update the version files and changelog together. Do not manually advance only
the package version or force-move an existing tag.

After 1.0.0, compatible fixes increment patch, compatible features increment
minor, and breaking changes increment major. Before 1.0.0, breaking changes
increment minor under the configured pre-major policy.

## Delivery channels

| Event | Images and tags |
| --- | --- |
| PR | Build affected images only; no publication |
| Ordinary `main` push | Publish affected images as `edge`, `main` and a unique `sha-<commit>-<run>-<attempt>` snapshot |
| Manual Docker or Hub workflow | Rebuild its images as unique snapshots; `edge`/`main` move only when running on `main` |
| RC version tag | Release all five images under exact version tags, without stable aliases |
| Stable version tag | Release all five images under exact version tags and move stable aliases after verification |

For `v1.2.3`, each image receives `1.2.3` and `v1.2.3`, plus mutable aliases
`1.2`, `1` and `latest`. Pre-major releases omit the broad `0` alias. RCs such
as `v0.1.0-rc.1` receive only `0.1.0-rc.1` and `v0.1.0-rc.1`.
Pin production deployments by `image@sha256:...`, not `edge` or `latest`.

## Coordinator and retry guarantees

1. Validate the version files and tag target; reject already published versions
   and out-of-order stable releases. Create or retain the draft release.
2. Run suite policy/unit checks, shell syntax checks, dependency audit and the
   Hub container smoke. A failed check prevents candidate builds.
3. Build all five images for AMD64 and ARM64 in parallel. Candidate images use
   GHCR `candidate-<version>` tags with source-revision and version labels.
   Retries reuse candidates only after validating both architectures and labels.
4. Collect image digests, per-platform SBOMs and provenance. Validate the complete
   five-image manifest before promoting any version tag.
5. Preflight every exact version tag in GHCR and configured Docker Hub. Missing
   tags may be created; an identical digest is reusable; a different digest fails
   without overwriting it. Authentication/network errors are not treated as absence.
6. Copy by digest, preserving the complete multi-architecture index. Verify every
   destination digest, then upload release assets and checksums.
7. Move stable aliases, recheck the Git tag target, and publish the GitHub Release.

Publication is serialized across suite releases. Registries and GitHub do not
provide a shared atomic transaction: a failure can leave some version tags or
aliases updated, while the GitHub Release remains a draft. Consumers should use
the completed GitHub Release manifest, not infer completion from one image tag.
Rerun failed jobs or dispatch **Release suite** with the same tag to resume using
the original candidate digests. Do not delete/rebuild candidates during recovery.
An already published version is refused. Upstream package/base-image changes
require a new suite version for release, or a unique development snapshot.

These are workflow-side safeguards, not server-enforced registry immutability.
Restrict package writers and Git tag updates; use registry immutability settings
where available. Never force-push a released version or manually overwrite its
image tags. The coordinator does not automatically roll back partial publication.

## Registries and release assets

GHCR publication uses `ghcr.io/<repository-owner>/<image>` and the workflow token.
Docker Hub is optional: repository secrets `DOCKERHUB_USERNAME` and
`DOCKERHUB_TOKEN` enable it; `DOCKERHUB_NAMESPACE` optionally overrides the
username. The account must be able to create/write all five image repositories.
Confirm package visibility and pull permissions before distributing references.

The GitHub Release contains `release-manifest.json`, per-image digest records,
SBOM and provenance JSON for both platforms, and `SHA256SUMS`. Candidate tags
are retained for recovery; clean them up only after separately verifying that
released images and their attestations remain available.

## Verification

```sh
node .github/scripts/release-policy.mjs
node --test .github/scripts/*.test.mjs
cd images/hub
npm ci --ignore-scripts
npm test
npm run check:public
docker build -t devtunnel-toolkit-hub:local .
npm run test:container
```

The coordinator's tests simulate GitHub and registry failures without publishing
anything. Real registry copying, GitHub release permissions and multi-architecture
attestation extraction must also pass in CI. A completed build is not deployment
acceptance; see the Hub's runtime, identity and storage release gates.
