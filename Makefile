DEVTUNNEL_IMAGE ?= devtunnel-toolkit:local
SQUID_IMAGE ?= devtunnel-toolkit-squid:local
OPENVPN_IMAGE ?= devtunnel-toolkit-openvpn:local
ROUTE_PROXY_ENV_FILE ?= .env.route-proxy
VOLUME ?= devtunnel-home
PORTS ?= 3140,53194
TTY ?= $(shell [ -t 0 ] || printf '%s' '-T')

RUN_DEVTUNNEL = PORTS="$(PORTS)" docker compose run --rm $(TTY) --no-deps devtunnel
ROUTE_PROXY_ENV_FILE_ARG = $(if $(wildcard $(ROUTE_PROXY_ENV_FILE)),--env-file "$(ROUTE_PROXY_ENV_FILE)",)
RUN_ROUTE_PROXY = docker compose $(ROUTE_PROXY_ENV_FILE_ARG) -f compose.route-proxy.yml

.PHONY: build login login-microsoft login-github logout status auth-check renew host connect up up-d down reset recreate logs proxy route-proxy-build route-proxy-up route-proxy-down route-proxy-logs vpn ovpn-client shell help

build:
	@docker compose build

login: build
	@$(RUN_DEVTUNNEL) login microsoft

login-microsoft: build
	@$(RUN_DEVTUNNEL) login microsoft

login-github: build
	@$(RUN_DEVTUNNEL) login github

logout:
	@$(RUN_DEVTUNNEL) logout

status:
	@$(RUN_DEVTUNNEL) status

auth-check: build
	@$(RUN_DEVTUNNEL) auth-check

renew: build auth-check
	@$(RUN_DEVTUNNEL) renew $(TUNNEL_ID) --once

host: up

up: build auth-check
	@PORTS="$(PORTS)" docker compose up

up-d: build auth-check
	@PORTS="$(PORTS)" docker compose up -d

connect:
	@$(RUN_DEVTUNNEL) connect $(TUNNEL_ID)

down:
	@docker compose down

reset:
	@docker compose down --volumes --remove-orphans

recreate: down up-d

logs:
	@docker compose logs -f

proxy: build auth-check
	@docker compose up squid devtunnel devtunnel-renew

route-proxy-build:
	@$(RUN_ROUTE_PROXY) build

route-proxy-up: route-proxy-build
	@$(RUN_ROUTE_PROXY) up -d

route-proxy-down:
	@$(RUN_ROUTE_PROXY) down

route-proxy-logs:
	@$(RUN_ROUTE_PROXY) logs -f route-proxy

vpn: build auth-check
	@docker compose up openvpn openvpn-network devtunnel devtunnel-renew

ovpn-client: build
	@docker compose run --rm $(TTY) openvpn client

shell: build
	@docker compose run --rm $(TTY) --entrypoint /bin/bash devtunnel

help:
	@$(RUN_DEVTUNNEL) help
