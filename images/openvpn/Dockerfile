# syntax=docker/dockerfile:1.7

FROM ubuntu:26.04@sha256:513c074113a871b51a8d16ab445c88779d6452d937a164fb5cc479f32668a41d

LABEL org.opencontainers.image.title="devtunnel-toolkit-openvpn" \
      org.opencontainers.image.description="OpenVPN server for DevTunnel Toolkit" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-toolkit" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-toolkit#readme" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        easy-rsa \
        iproute2 \
        iptables \
        openvpn \
        openssl \
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

COPY images/openvpn/openvpn-entrypoint /usr/local/bin/openvpn-entrypoint

RUN chmod +x /usr/local/bin/openvpn-entrypoint \
    && groupadd --gid 10001 vpn \
    && useradd --uid 10001 --gid vpn --no-create-home --home-dir /etc/openvpn --shell /usr/sbin/nologin vpn \
    && mkdir -p /etc/openvpn && chown vpn:vpn /etc/openvpn

EXPOSE 53194/tcp

VOLUME ["/etc/openvpn"]

USER 10001:10001

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/openvpn-entrypoint"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 CMD ["/usr/local/bin/openvpn-entrypoint", "healthcheck"]
CMD ["server"]
