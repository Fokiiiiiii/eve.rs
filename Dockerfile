# syntax=docker/dockerfile:1
#
# The eve.rs server in one image: the Rust front (eve-net), the Rust space simulation (eve-sim), the static-data
# tool (eve-sde) and the C# service host (eve-host). docker-compose.yml runs them as separate services.

FROM rust:1.99.1-bookworm@sha256:e6f2cff35d0f309acf8433b4f05878ed77092602c5065cc81801177da0bb3a9c AS rust
WORKDIR /src
COPY Cargo.toml Cargo.lock ./
COPY crates crates
# eve-sde embeds the lock of the static data it may fetch
COPY sde/lock.json sde/lock.json
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/src/target \
    cargo build --release --locked -p eve-net -p eve-sim -p eve-sde \
 && mkdir -p /out \
 && cp target/release/eve-net target/release/eve-sim target/release/eve-sde /out/

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS dotnet
WORKDIR /src
COPY dotnet dotnet
RUN dotnet publish dotnet/src/Eve.Host -c Release -o /out/host --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:4f1f30cf0bb4ab0c8b2daa84613051683456f8df4a9bf7c812e20d74962992ac AS runtime
# curl does eve-sde's downloading
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY --from=rust /out/ /usr/local/bin/
COPY --from=dotnet /out/host /opt/eve-host
# the simulation finds the static data under .cache/sde of its working directory
RUN useradd --system --uid 10001 --home-dir /data eve \
 && mkdir -p /data/.cache/sde /run/eve \
 && chown -R eve /data /run/eve
USER eve
WORKDIR /data
ENV DOTNET_EnableDiagnostics=0
CMD ["eve-net"]
