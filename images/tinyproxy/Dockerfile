# syntax=docker/dockerfile:1.7

FROM debian:trixie-slim@sha256:d7e12182ce18b85b93007c1dedf31f2d29e01ccf3182cc4017c709b6259bc132

LABEL org.opencontainers.image.title="devtunnel-toolkit-route-proxy" \
      org.opencontainers.image.description="Selective local route proxy for DevTunnel Toolkit clients" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-toolkit" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-toolkit#selective-local-proxy" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends \
        tini \
        tinyproxy-bin \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system tinyproxy \
    && useradd --system --gid tinyproxy --no-create-home \
        --home-dir /nonexistent --shell /usr/sbin/nologin tinyproxy

COPY tinyproxy.conf.template /etc/tinyproxy/tinyproxy.conf.template
COPY tinyproxy-entrypoint /usr/local/bin/tinyproxy-entrypoint

RUN chmod +x /usr/local/bin/tinyproxy-entrypoint \
    && mkdir -p /run/tinyproxy /var/log/tinyproxy \
    && chown -R tinyproxy:tinyproxy /run/tinyproxy /var/log/tinyproxy

EXPOSE 8888

USER tinyproxy

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/tinyproxy-entrypoint"]
CMD ["tinyproxy"]
