# syntax=docker/dockerfile:1.7

FROM ubuntu:26.04@sha256:513c074113a871b51a8d16ab445c88779d6452d937a164fb5cc479f32668a41d

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

RUN --mount=type=bind,source=images/hub/bin/use-gnu-coreutils,target=/tmp/use-gnu-coreutils \
    apt-get update && sh /tmp/use-gnu-coreutils validate-plan \
    && apt-get install -y --no-install-recommends --allow-remove-essential \
       coreutils-from-gnu coreutils-from-uutils- rust-coreutils- \
    && sh /tmp/use-gnu-coreutils verify-installed \
    && test -f /usr/bin/pebble && test ! -L /usr/bin/pebble \
    && ! dpkg-query --search /usr/bin/pebble \
    && rm /usr/bin/pebble && rm -rf /var/lib/apt/lists/*

COPY images/tinyproxy/tinyproxy.conf.template /etc/tinyproxy/tinyproxy.conf.template
COPY images/tinyproxy/tinyproxy-entrypoint /usr/local/bin/tinyproxy-entrypoint

RUN chmod +x /usr/local/bin/tinyproxy-entrypoint \
    && mkdir -p /run/tinyproxy /var/log/tinyproxy \
    && chown -R tinyproxy:tinyproxy /run/tinyproxy /var/log/tinyproxy

EXPOSE 8888

USER tinyproxy

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/tinyproxy-entrypoint"]
CMD ["tinyproxy"]
