# syntax=docker/dockerfile:1.7

FROM node:26-trixie-slim@sha256:14bf3eac4bf209d906d3c41256597d3ab1f926b2e93a79e9bdfe1efd32454239 AS node-build
# Use the same source-verified library recipe as the Hub, compiled for Ubuntu's ABI.
FROM ubuntu:26.04@sha256:513c074113a871b51a8d16ab445c88779d6452d937a164fb5cc479f32668a41d AS native
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential binutils dpkg-dev ca-certificates curl xz-utils patch \
    cmake meson ninja-build pkg-config gettext libffi-dev libpcre2-dev \
    libmount-dev libselinux1-dev zlib1g-dev libtasn1-6-dev libsystemd-dev \
    dbus-daemon dbus-bin libexpat1 libglib2.0-0t64 libp11-kit0 p11-kit p11-kit-modules
RUN apt-get install -y --no-install-recommends libtasn1-bin
COPY --from=node-build /usr/local/bin/node /usr/local/bin/node
COPY images/hub/bin/build-native-libraries /build-tools/
RUN sh /build-tools/build-native-libraries sources
RUN sh /build-tools/build-native-libraries expat
RUN sh /build-tools/build-native-libraries glib
COPY images/hub/bin/p11-kit-module-soname.patch /native/sources/
RUN apt-get install -y --no-install-recommends systemd-dev \
    && patch --batch --forward --fuzz=0 -p1 -d /native/sources/p11-kit-0.26.5 \
       < /native/sources/p11-kit-module-soname.patch \
    && sh /build-tools/build-native-libraries p11kit
COPY images/hub/bin/build-mime-backport images/hub/bin/native-mime-regression.c /build-tools/
RUN sh /build-tools/build-mime-backport
COPY images/hub/bin/package-native-libraries.mjs images/hub/bin/native-metadata.mjs /build-tools/
RUN sh /build-tools/build-native-libraries package

FROM ubuntu:26.04@sha256:513c074113a871b51a8d16ab445c88779d6452d937a164fb5cc479f32668a41d

ARG TARGETARCH
ARG DEVTUNNEL_ENV=prod
ARG DEVTUNNEL_VERSION=1.0.2030
ARG DEVTUNNEL_SHA256

LABEL org.opencontainers.image.title="devtunnel-toolkit" \
      org.opencontainers.image.description="Developer toolkit for Microsoft Dev Tunnels" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-toolkit" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-toolkit#readme" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        dbus-x11 \
        gnome-keyring \
        libicu78 \
        libsecret-1-0 \
        systemd-standalone-sysusers \
        tini \
    && rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
        amd64) devtunnel_arch="x64"; devtunnel_checksum=ff6911548907b5abaea4ed5baa36b2420be7c5debcb637a4f50f7a4002b10b60 ;; \
        arm64) devtunnel_arch="arm64"; devtunnel_checksum=f7a76e0117a3e8d5bfbf9416e3480cdac36c2b4bb10d2683f0780dc9284b642f ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH:-unknown}" >&2; exit 1 ;; \
    esac; \
    test "$DEVTUNNEL_ENV" = prod || test -n "$DEVTUNNEL_SHA256"; \
    curl --fail --silent --show-error --location \
       --retry 3 --connect-timeout 15 --max-time 180 --proto '=https' --tlsv1.2 \
        -o /usr/local/bin/devtunnel \
        "https://tunnelsassets${DEVTUNNEL_ENV}.blob.core.windows.net/cli/linux-${devtunnel_arch}-devtunnel"; \
    echo "${DEVTUNNEL_SHA256:-$devtunnel_checksum}  /usr/local/bin/devtunnel" | sha256sum -c -; \
    chmod +x /usr/local/bin/devtunnel; \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/devtunnel-check devtunnel --version | grep -F "$DEVTUNNEL_VERSION"; \
    mkdir -p /usr/local/share/devtunnel; \
    find /tmp/devtunnel-check -name devtunnel.deps.json \
       -exec cp '{}' /usr/local/share/devtunnel/devtunnel.deps.json \;; \
    rm -rf /tmp/devtunnel-check; \
    apt-get purge -y --auto-remove curl

RUN --mount=type=bind,source=images/hub/bin/use-gnu-coreutils,target=/tmp/use-gnu-coreutils \
    apt-get update && sh /tmp/use-gnu-coreutils validate-plan \
    && apt-get install -y --no-install-recommends --allow-remove-essential \
       coreutils-from-gnu coreutils-from-uutils- rust-coreutils- \
    && sh /tmp/use-gnu-coreutils verify-installed \
    && test -f /usr/bin/pebble && test ! -L /usr/bin/pebble \
    && ! dpkg-query --search /usr/bin/pebble \
    && rm /usr/bin/pebble && rm -rf /var/lib/apt/lists/*
RUN --mount=type=bind,from=native,source=/native/debs,target=/native-debs \
    apt-get update && apt-get install -y --no-install-recommends /native-debs/*.deb \
    && test -z "$(dpkg --audit)" && rm -rf /var/lib/apt/lists/*
COPY --from=native /native/evidence/ /usr/local/share/devtunnel/native/evidence/
COPY --from=native /native/sources/*.tar.xz /native/sources/*.patch /usr/local/share/devtunnel/native/sources/
COPY images/hub/bin/build-native-libraries images/hub/bin/package-native-libraries.mjs images/hub/bin/native-metadata.mjs images/hub/bin/build-mime-backport images/hub/bin/native-mime-regression.c /usr/local/share/devtunnel/native/recipe/

RUN test "$(id -u ubuntu)" = 1000 && test "$(id -g ubuntu)" = 1000 \
    && groupmod --new-name devtunnel ubuntu \
    && usermod --login devtunnel --home /home/devtunnel --move-home --shell /bin/bash --comment '' ubuntu \
    && mkdir -p /workspace \
    && chown devtunnel:devtunnel /workspace

COPY docker/devtunnel-entrypoint /usr/local/bin/devtunnel-entrypoint

RUN chmod +x /usr/local/bin/devtunnel-entrypoint

USER devtunnel

ENV HOME=/home/devtunnel \
    XDG_CONFIG_HOME=/home/devtunnel/.config \
    XDG_CACHE_HOME=/home/devtunnel/.cache \
    XDG_DATA_HOME=/home/devtunnel/.local/share

WORKDIR /workspace
VOLUME ["/home/devtunnel"]

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/devtunnel-entrypoint"]
HEALTHCHECK --interval=30s --timeout=8s --start-period=15s --retries=3 CMD ["/usr/local/bin/devtunnel-entrypoint", "healthcheck"]
CMD ["help"]
