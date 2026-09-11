# syntax=docker/dockerfile:1.7

FROM debian:trixie-slim@sha256:d7e12182ce18b85b93007c1dedf31f2d29e01ccf3182cc4017c709b6259bc132

LABEL org.opencontainers.image.title="devtunnel-toolkit-squid" \
      org.opencontainers.image.description="Squid proxy for DevTunnel Toolkit" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-toolkit" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-toolkit#readme" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        squid \
        systemd-standalone-sysusers \
        tini \
    && rm -rf /var/lib/apt/lists/*

COPY squid.conf.template /etc/squid/squid.conf.template
COPY squid-entrypoint /usr/local/bin/squid-entrypoint

RUN chmod +x /usr/local/bin/squid-entrypoint \
    && mkdir -p /var/cache/squid /var/log/squid \
    && chown -R proxy:proxy /var/cache/squid /var/log/squid

EXPOSE 3140

USER proxy
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s CMD squid -k check -f /tmp/squid.conf || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/squid-entrypoint"]
CMD ["squid"]
