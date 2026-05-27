# syntax=docker/dockerfile:1.7

FROM debian:bookworm-slim

LABEL org.opencontainers.image.title="devtunnel-toolkit-squid" \
      org.opencontainers.image.description="Squid proxy for DevTunnel Toolkit" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-toolkit" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-toolkit#readme" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        squid \
        tini \
    && rm -rf /var/lib/apt/lists/*

COPY squid.conf.template /etc/squid/squid.conf.template
COPY squid-entrypoint /usr/local/bin/squid-entrypoint

RUN chmod +x /usr/local/bin/squid-entrypoint \
    && mkdir -p /var/cache/squid /var/log/squid \
    && chown -R proxy:proxy /var/cache/squid /var/log/squid

EXPOSE 3140

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/squid-entrypoint"]
CMD ["squid"]
