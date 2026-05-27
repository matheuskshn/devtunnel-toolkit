DEVTUNNEL_IMAGE ?= devtunnel-toolkit:local
SQUID_IMAGE ?= devtunnel-toolkit-squid:local
OPENVPN_IMAGE ?= devtunnel-toolkit-openvpn:local
VOLUME ?= devtunnel-home
PORTS ?= 3140,1194
TTY ?= $(shell [ -t 0 ] || printf '%s' '-T')

RUN_DEVTUNNEL = PORTS="$(PORTS)" docker compose run --rm $(TTY) --no-deps devtunnel

.PHONY: build login login-microsoft login-github logout status host connect up down logs proxy vpn ovpn-client shell help

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

host: up

up: build
	@PORTS="$(PORTS)" docker compose up

connect:
	@$(RUN_DEVTUNNEL) connect $(TUNNEL_ID)

down:
	@docker compose down

logs:
	@docker compose logs -f

proxy: build
	@docker compose up squid devtunnel

vpn: build
	@docker compose up openvpn devtunnel

ovpn-client: build
	@docker compose run --rm $(TTY) openvpn client

shell: build
	@docker compose run --rm $(TTY) --entrypoint /bin/bash devtunnel

help:
	@$(RUN_DEVTUNNEL) help
