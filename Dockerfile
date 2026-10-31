# syntax=docker/dockerfile:1.7

FROM node:24-trixie-slim@sha256:6950b66b4c0cb0151ce89fa75074673850763d096b044f422c6729b588dd4956 AS node-build

FROM node:24-trixie-slim@sha256:6950b66b4c0cb0151ce89fa75074673850763d096b044f422c6729b588dd4956 AS cli
ARG TARGETARCH
ARG DEVTUNNEL_ENV=prod
ARG DEVTUNNEL_VERSION=1.0.2115
ARG DEVTUNNEL_SHA256
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl libicu76
COPY images/hub/bin/managed-cli.mjs images/hub/bin/install-managed-cli.mjs images/hub/bin/devtunnel-runtime.json /build-tools/
RUN node /build-tools/install-managed-cli.mjs "$TARGETARCH" "$DEVTUNNEL_VERSION" "$DEVTUNNEL_SHA256" "$DEVTUNNEL_ENV"

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

LABEL org.opencontainers.image.title="devtunnel-toolkit" \
      org.opencontainers.image.description="Developer toolkit for Microsoft Dev Tunnels" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-toolkit" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-toolkit#readme" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        dbus-x11 \
        gnome-keyring \
        libicu78 \
        libssl3t64 \
        libsecret-1-0 \
        systemd-standalone-sysusers \
        tini \
    && rm -rf /var/lib/apt/lists/*

COPY --from=cli /out/ /opt/devtunnel/
COPY --chmod=755 images/hub/bin/devtunnel-cli /usr/local/bin/devtunnel

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
