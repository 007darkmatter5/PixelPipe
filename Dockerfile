# syntax=docker/dockerfile:1

FROM rust:1.100-bookworm AS engine-build
RUN apt-get update && apt-get install -y --no-install-recommends \
    pkg-config clang libclang-dev \
    libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev libopus-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY engine/ /src/
RUN cargo build --release

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS ui-build
WORKDIR /src
COPY ui/ /src/
RUN dotnet publish PixelPipe.Web/PixelPipe.Web.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    libavcodec59 libavformat59 libavutil57 libswscale6 libswresample4 libopus0 \
    curl unzip ca-certificates \
    && rm -rf /var/lib/apt/lists/*
ARG TARGETARCH
RUN set -eux; \
    case "$TARGETARCH" in \
      amd64) CORE_ARCH=x86_64 ;; \
      arm64) CORE_ARCH=arm64 ;; \
      *) echo "unsupported architecture $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    mkdir -p /cores; \
    for core in fceumm snes9x genesis_plus_gx; do \
      curl -fsSL -o /tmp/core.zip "https://buildbot.libretro.com/nightly/linux/${CORE_ARCH}/latest/${core}_libretro.so.zip"; \
      unzip -o /tmp/core.zip -d /cores; \
    done; \
    rm -f /tmp/core.zip
COPY --from=engine-build /src/target/release/pixelpipe-engine /usr/local/bin/pixelpipe-engine
COPY --from=ui-build /out /opt/pixelpipe/ui
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    PIXELPIPE_BIND=0.0.0.0:9090 \
    PIXELPIPE_ROMS=/roms \
    PIXELPIPE_CORES=/cores \
    PIXELPIPE_DATA=/data \
    PIXELPIPE_WEBRTC_PORT_MIN=50000 \
    PIXELPIPE_WEBRTC_PORT_MAX=50100 \
    PixelPipe__RomsPath=/roms \
    PixelPipe__EngineUrl=http://127.0.0.1:9090 \
    PixelPipe__PublicEngineUrl=
EXPOSE 8080 9090
VOLUME ["/roms", "/data"]
ENTRYPOINT ["/entrypoint.sh"]
