# syntax=docker/dockerfile:1.7

FROM debian:trixie-slim@sha256:d7e12182ce18b85b93007c1dedf31f2d29e01ccf3182cc4017c709b6259bc132

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
        libicu76 \
        libsecret-1-0 \
        tini \
    && rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
        amd64) devtunnel_arch="x64"; devtunnel_checksum=ff6911548907b5abaea4ed5baa36b2420be7c5debcb637a4f50f7a4002b10b60 ;; \
        arm64) devtunnel_arch="arm64"; devtunnel_checksum=f7a76e0117a3e8d5bfbf9416e3480cdac36c2b4bb10d2683f0780dc9284b642f ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH:-unknown}" >&2; exit 1 ;; \
    esac; \
    test "$DEVTUNNEL_ENV" = prod || test -n "$DEVTUNNEL_SHA256"; \
    curl --fail --silent --show-error --location --retry 3 --connect-timeout 15 --max-time 180 --proto '=https' --tlsv1.2 \
        -o /usr/local/bin/devtunnel \
        "https://tunnelsassets${DEVTUNNEL_ENV}.blob.core.windows.net/cli/linux-${devtunnel_arch}-devtunnel"; \
    echo "${DEVTUNNEL_SHA256:-$devtunnel_checksum}  /usr/local/bin/devtunnel" | sha256sum -c -; \
    chmod +x /usr/local/bin/devtunnel; \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/devtunnel-check devtunnel --version | grep -F "$DEVTUNNEL_VERSION"; \
    mkdir -p /usr/local/share/devtunnel; \
    find /tmp/devtunnel-check -name devtunnel.deps.json -exec cp '{}' /usr/local/share/devtunnel/devtunnel.deps.json \;; \
    rm -rf /tmp/devtunnel-check; \
    apt-get purge -y --auto-remove curl

RUN groupadd --gid 1000 devtunnel \
    && useradd --uid 1000 --gid devtunnel --create-home --shell /bin/bash devtunnel \
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
CMD ["help"]
