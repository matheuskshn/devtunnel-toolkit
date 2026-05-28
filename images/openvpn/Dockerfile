# syntax=docker/dockerfile:1.7

FROM debian:bookworm-slim

LABEL org.opencontainers.image.title="devtunnel-toolkit-openvpn" \
      org.opencontainers.image.description="OpenVPN server for DevTunnel Toolkit" \
      org.opencontainers.image.source="https://github.com/matheuskshn/devtunnel-toolkit" \
      org.opencontainers.image.documentation="https://github.com/matheuskshn/devtunnel-toolkit#readme" \
      org.opencontainers.image.licenses="MIT"

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        easy-rsa \
        iproute2 \
        iptables \
        openvpn \
        openssl \
        procps \
        tini \
    && rm -rf /var/lib/apt/lists/*

COPY openvpn-entrypoint /usr/local/bin/openvpn-entrypoint

RUN chmod +x /usr/local/bin/openvpn-entrypoint

EXPOSE 53194/tcp

VOLUME ["/etc/openvpn"]

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/openvpn-entrypoint"]
CMD ["server"]
