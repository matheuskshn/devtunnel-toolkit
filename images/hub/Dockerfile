# syntax=docker/dockerfile:1
FROM node:22-bookworm-slim AS build
WORKDIR /build
COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts
COPY tsconfig.json ./
COPY src/ ./src/
RUN npm run build && npm prune --omit=dev --ignore-scripts

FROM debian:bookworm-slim
ARG TARGETARCH
ARG DEVTUNNEL_VERSION=1.0.2030
ARG DEVTUNNEL_SHA256
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl dbus dbus-x11 gnome-keyring libicu72 libsecret-1-0 libsecret-tools \
    libstdc++6 squid tini util-linux \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1000 --create-home --shell /usr/sbin/nologin hub \
    && mkdir -p /data /config /run/hub /opt/hub \
    && chown hub:hub /data /run/hub
RUN case "${TARGETARCH}" in \
      amd64) hub_arch=x64; hub_checksum=ff6911548907b5abaea4ed5baa36b2420be7c5debcb637a4f50f7a4002b10b60 ;; \
      arm64) hub_arch=arm64; hub_checksum=f7a76e0117a3e8d5bfbf9416e3480cdac36c2b4bb10d2683f0780dc9284b642f ;; \
      *) exit 1 ;; esac \
    && curl --silent --show-error --fail --location --retry 3 --proto '=https' --tlsv1.2 \
       "https://tunnelsassetsprod.blob.core.windows.net/cli/linux-${hub_arch}-devtunnel" -o /usr/local/bin/devtunnel \
    && echo "${DEVTUNNEL_SHA256:-$hub_checksum}  /usr/local/bin/devtunnel" | sha256sum -c - \
    && chmod 755 /usr/local/bin/devtunnel \
    && /usr/local/bin/devtunnel --version | grep -F "${DEVTUNNEL_VERSION}"
COPY --from=build /usr/local/bin/node /usr/local/bin/node
COPY --from=build /build/dist /opt/hub/dist
COPY --from=build /build/node_modules /opt/hub/node_modules
COPY --from=build /build/package.json /opt/hub/package.json
COPY --chmod=755 bin/hub /usr/local/bin/hub
USER 1000:1000
WORKDIR /opt/hub
ENV HOME=/home/hub
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s CMD ["hub", "health"]
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "/usr/local/bin/hub"]
CMD ["serve"]
