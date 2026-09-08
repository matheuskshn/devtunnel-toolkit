# Docker

Build from the repository root:

```sh
docker build -t devtunnel-toolkit-hub:local images/hub
```

Create an external configuration file from `../config.example.json`. Keep real
destinations and credentials outside this checkout. Set `HUB_CONFIG_FILE` to its
absolute path, then:

```sh
docker run -d --name devtunnel-hub \
  --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /run/hub:uid=1000,gid=1000,mode=0700 --tmpfs /tmp:mode=1777 \
  --mount type=volume,src=devtunnel-hub-data,dst=/data \
  --mount "type=bind,src=${HUB_CONFIG_FILE},dst=/config/hub.json,readonly" \
  devtunnel-toolkit-hub:local

docker exec devtunnel-hub hub session add user-a --provider microsoft --tunnel-name devhub-user-a
docker exec -it devtunnel-hub hub session login user-a
docker exec devtunnel-hub hub session start user-a
docker exec devtunnel-hub hub session status user-a
```

The device code is shown only in the administrative terminal. Users must not be
given Docker access simply to perform login. A trusted operator initiates each
login and the corresponding user completes it in their own browser.

For environment-only configuration, replace the configuration bind mount with
`--env-file /opt/devtunnel-hub/hub.env`. See the [environment variable reference](../../README.md#configuration-and-policy).
Keep the data volume and runtime mounts unchanged. Do not set `HUB_CONFIG` unless
also mounting that JSON file. Docker passes the file's variables to the container;
the Hub does not read dotenv files itself.

On the user's machine, log into the same provider/account and connect using the
canonical `tunnel_id` shown by status:

```sh
devtunnel user login --entra --use-device-code-auth
devtunnel connect devhub-user-a.use1
```

Configure the client application's HTTP proxy as `http://127.0.0.1:3140`.
The cluster suffix above is an example, not a preset. Different users use their
own tunnel and the same local port. Two simultaneous connections on the SAME
client machine still cannot both bind that port.
