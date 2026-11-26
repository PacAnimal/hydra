# The linux lane: the whole suite on the newest Ubuntu with .NET 10, under Xvfb, with the runtimes Styx.md's reference
# code needs (Node LTS, stable Rust, Python). run-tests.sh builds it with --pull, so every base is the newest of its tag.
FROM node:lts-slim AS node
FROM rust:slim AS rust
FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute

RUN apt-get update \
	&& DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
		xvfb xauth xclip procps libx11-6 libxi6 libxfixes3 libxtst6 libxkbcommon0 xkb-data libcairo2 fonts-dejavu-core \
		python3 gcc libc6-dev iproute2 \
	&& rm -rf /var/lib/apt/lists/*

COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=rust /usr/local/rustup /usr/local/rustup
COPY --from=rust /usr/local/cargo/bin /usr/local/cargo/bin
ENV RUSTUP_HOME=/usr/local/rustup PATH=/usr/local/cargo/bin:$PATH

# the suite runs as this user, not root: tests of what a user may not read or open skip for root
RUN useradd --create-home --uid 2000 hydra-test

COPY lane.sh /usr/local/bin/lane.sh
RUN chmod +x /usr/local/bin/lane.sh
ENTRYPOINT ["/usr/local/bin/lane.sh"]
