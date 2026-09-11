# syntax=docker/dockerfile:1
FROM node:24-trixie-slim@sha256:6950b66b4c0cb0151ce89fa75074673850763d096b044f422c6729b588dd4956 AS build
WORKDIR /build
COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts
COPY tsconfig.json ./
COPY src/ ./src/
RUN npm run build && npm prune --omit=dev --ignore-scripts

FROM debian:trixie-slim@sha256:d7e12182ce18b85b93007c1dedf31f2d29e01ccf3182cc4017c709b6259bc132
ARG TARGETARCH
ARG DEVTUNNEL_VERSION=1.0.2030
ARG DEVTUNNEL_SHA256
# Select the packaged session-bus and sysusers providers that do not require
# a systemd/PAM login stack. Sessions still launch private dbus-daemon/keyrings.
RUN apt-get update && apt-get upgrade -y && apt-get install -y --no-install-recommends \
    ca-certificates curl dbus-x11 gnome-keyring libicu76 libsecret-1-0 libsecret-tools \
    libstdc++6 squid systemd-standalone-sysusers tini util-linux \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1000 --create-home --shell /usr/sbin/nologin hub \
    && mkdir -p /data /config /run/hub /opt/hub \
    && chown hub:hub /data /run/hub
RUN case "${TARGETARCH}" in \
      amd64) hub_arch=x64; hub_checksum=ff6911548907b5abaea4ed5baa36b2420be7c5debcb637a4f50f7a4002b10b60 ;; \
      arm64) hub_arch=arm64; hub_checksum=f7a76e0117a3e8d5bfbf9416e3480cdac36c2b4bb10d2683f0780dc9284b642f ;; \
      *) exit 1 ;; esac \
    && curl --silent --show-error --fail --location --retry 3 --connect-timeout 15 --max-time 180 --proto '=https' --tlsv1.2 \
       "https://tunnelsassetsprod.blob.core.windows.net/cli/linux-${hub_arch}-devtunnel" -o /usr/local/bin/devtunnel \
    && echo "${DEVTUNNEL_SHA256:-$hub_checksum}  /usr/local/bin/devtunnel" | sha256sum -c - \
    && chmod 755 /usr/local/bin/devtunnel \
    && DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/devtunnel-check /usr/local/bin/devtunnel --version | grep -F "${DEVTUNNEL_VERSION}" \
    && mkdir -p /usr/local/share/devtunnel \
    && find /tmp/devtunnel-check -name devtunnel.deps.json -exec cp '{}' /usr/local/share/devtunnel/devtunnel.deps.json \; \
    && rm -rf /tmp/devtunnel-check \
    && apt-get purge -y --auto-remove curl
COPY --from=build /usr/local/bin/node /usr/local/bin/node
COPY --from=build /build/dist /opt/hub/dist
COPY --from=build /build/node_modules /opt/hub/node_modules
COPY --from=build /build/package.json /opt/hub/package.json
COPY web/ /opt/hub/web/
COPY --chmod=755 bin/hub /usr/local/bin/hub
USER 1000:1000
WORKDIR /opt/hub
ENV HOME=/home/hub
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s CMD ["hub", "health"]
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "/usr/local/bin/hub"]
CMD ["serve"]
