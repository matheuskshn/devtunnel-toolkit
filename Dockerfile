# syntax=docker/dockerfile:1.7

FROM debian:bookworm-slim

ARG TARGETARCH
ARG DEVTUNNEL_ENV=prod

LABEL org.opencontainers.image.title="devtunnel-container" \
      org.opencontainers.image.description="Container image for the Microsoft devtunnel CLI" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-container" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-container#readme" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        libicu72 \
        libsecret-1-0 \
        tini \
    && rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
        amd64) devtunnel_arch="x64" ;; \
        arm64) devtunnel_arch="arm64" ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH:-unknown}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL \
        -o /usr/local/bin/devtunnel \
        "https://tunnelsassets${DEVTUNNEL_ENV}.blob.core.windows.net/cli/linux-${devtunnel_arch}-devtunnel"; \
    chmod +x /usr/local/bin/devtunnel; \
    devtunnel --version

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
