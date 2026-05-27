IMAGE ?= devtunnel:local
VOLUME ?= devtunnel-home
PORTS ?= 3140
TTY ?= $(shell [ -t 0 ] && printf '%s' '-it')

RUN = docker run --rm $(TTY) \
	--network host \
	-v $(VOLUME):/home/devtunnel \
	-e PORTS="$(PORTS)" \
	-e TUNNEL_ID \
	-e ALLOW_ANONYMOUS \
	-e PROTOCOL \
	-e EXPIRATION \
	-e VERBOSE \
	-e LOGIN_PROVIDER \
	$(IMAGE)

.PHONY: build login login-microsoft login-github logout status host connect shell help

build:
	@docker build -t $(IMAGE) .

login: build
	@$(RUN) login microsoft

login-microsoft: build
	@$(RUN) login microsoft

login-github: build
	@$(RUN) login github

logout:
	@$(RUN) logout

status:
	@$(RUN) status

host: build
	@$(RUN) host

connect:
	@$(RUN) connect $(TUNNEL_ID)

shell: build
	@docker run --rm $(TTY) --network host -v $(VOLUME):/home/devtunnel --entrypoint /bin/bash $(IMAGE)

help:
	@$(RUN) help
