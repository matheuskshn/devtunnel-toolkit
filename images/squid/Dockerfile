# syntax=docker/dockerfile:1.7

FROM ubuntu:26.04@sha256:cd21a4f68a617580279d4b091cb18e3af9fa8a87500665f0ae5f7f757d17d367

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

RUN --mount=type=bind,source=images/hub/bin/use-gnu-coreutils,target=/tmp/use-gnu-coreutils \
    apt-get update && sh /tmp/use-gnu-coreutils validate-plan \
    && apt-get install -y --no-install-recommends --allow-remove-essential \
       coreutils-from-gnu coreutils-from-uutils- rust-coreutils- \
    && sh /tmp/use-gnu-coreutils verify-installed \
    && test -f /usr/bin/pebble && test ! -L /usr/bin/pebble \
    && ! dpkg-query --search /usr/bin/pebble \
    && rm /usr/bin/pebble && rm -rf /var/lib/apt/lists/*

COPY images/squid/squid.conf.template /etc/squid/squid.conf.template
COPY images/squid/squid-entrypoint /usr/local/bin/squid-entrypoint

RUN chmod +x /usr/local/bin/squid-entrypoint \
    && mkdir -p /var/cache/squid /var/log/squid \
    && chown -R proxy:proxy /var/cache/squid /var/log/squid

EXPOSE 3140

USER proxy
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s CMD squid -k check -f /tmp/squid.conf || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/squid-entrypoint"]
CMD ["squid"]
