# Streamsemble hub image.
#
# Runtime dependencies that are NOT optional:
#   ffmpeg   — AAC encoder for the buffered AirPlay 2 stream (the verified path
#              for AirPlay 2 speakers/TVs). No ffmpeg, no buffered playback.
#   librespot — the Spotify Connect source (built from source below, with the
#              repo's resilience patch applied; see tools/).
#   curl     — librespot's --onevent helper script POSTs player events with it.
#
# The container must run with host networking: mDNS (5353 multicast), PTP
# (319/320) and the dynamically allocated RTP/RTCP/timing/control UDP ports all
# have to live on the LAN the speakers are on. See docker-compose.yml.

# librespot fork: "none" skips the Rust build entirely (no Spotify source;
# run with Spotify__Enabled=false). Ref 9c7d756 is the upstream dev commit the
# repo's resilience patch was cut against — bump both together.
ARG LIBRESPOT_VARIANT=fork
ARG LIBRESPOT_REPO=https://github.com/librespot-org/librespot.git
ARG LIBRESPOT_REF=9c7d756

# ---------------------------------------------------------------- .NET build --
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
WORKDIR /src

# Project files first so `restore` caches independently of source edits.
COPY Directory.Build.props ./
COPY src/Streamsemble.Core/Streamsemble.Core.csproj                       src/Streamsemble.Core/
COPY src/Streamsemble.Timing/Streamsemble.Timing.csproj                   src/Streamsemble.Timing/
COPY src/Streamsemble.Discovery/Streamsemble.Discovery.csproj             src/Streamsemble.Discovery/
COPY src/Streamsemble.AirPlay.Common/Streamsemble.AirPlay.Common.csproj   src/Streamsemble.AirPlay.Common/
COPY src/Streamsemble.AirPlay.Sender/Streamsemble.AirPlay.Sender.csproj   src/Streamsemble.AirPlay.Sender/
COPY src/Streamsemble.AirPlay.Receiver/Streamsemble.AirPlay.Receiver.csproj src/Streamsemble.AirPlay.Receiver/
COPY src/Streamsemble.Spotify/Streamsemble.Spotify.csproj                 src/Streamsemble.Spotify/
COPY src/Streamsemble.Cast.Stub/Streamsemble.Cast.Stub.csproj             src/Streamsemble.Cast.Stub/
COPY src/Streamsemble.Host/Streamsemble.Host.csproj                       src/Streamsemble.Host/
RUN dotnet restore src/Streamsemble.Host/Streamsemble.Host.csproj

COPY src/ src/
RUN dotnet publish src/Streamsemble.Host/Streamsemble.Host.csproj \
        -c Release --no-restore -o /app

# ------------------------------------------------------------ librespot fork --
# Upstream librespot exits when Spotify's server drops the session; the patch in
# tools/ makes it re-announce and reclaim the orphaned playback session.
FROM rust:slim-bookworm AS librespot-fork
RUN apt-get update && apt-get install -y --no-install-recommends \
        git ca-certificates pkg-config build-essential libssl-dev libasound2-dev \
    && rm -rf /var/lib/apt/lists/*
ARG LIBRESPOT_REPO
ARG LIBRESPOT_REF
WORKDIR /build
COPY tools/librespot-resilience.patch /tmp/librespot-resilience.patch
RUN git clone "$LIBRESPOT_REPO" librespot \
    && git -C librespot checkout --detach "$LIBRESPOT_REF" \
    && git -C librespot apply --3way /tmp/librespot-resilience.patch
RUN cargo build --release --manifest-path librespot/Cargo.toml \
    && install -D -m 0755 librespot/target/release/librespot /out/librespot

# Opt-out stage: same COPY shape, empty payload (reuses an image already pulled).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS librespot-none
RUN mkdir -p /out

FROM librespot-${LIBRESPOT_VARIANT} AS librespot

# -------------------------------------------------------------------- runtime --
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
        ffmpeg curl libasound2 libcap2-bin ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=librespot   /out/ /usr/local/bin/
COPY --from=dotnet-build /app  /app

# HOME drives ~/.streamsemble (HomeKit pairings, PIN file, librespot cache) —
# mount /data to keep pairing identity and Spotify credentials across restarts.
ENV HOME=/data \
    ASPNETCORE_URLS=http://0.0.0.0:8088 \
    DOTNET_EnableDiagnostics=0
# Runs unprivileged; the file capability is what lets the PTP grandmaster still
# bind UDP 319/320 (the container also needs cap_add: NET_BIND_SERVICE, which is
# where the capability comes from — with it, the process keeps
# cap_net_bind_service in its effective set).
RUN useradd --system --uid 10001 --home-dir /data --shell /usr/sbin/nologin streamsemble \
    && mkdir -p /data && chown streamsemble:streamsemble /data \
    && setcap cap_net_bind_service=+ep /app/Streamsemble.Host

VOLUME /data
WORKDIR /app
USER streamsemble

# Informational under host networking (nothing is published/mapped):
#   8088/tcp web UI + REST API   7000/tcp AirPlay receiver RTSP
#   319+320/udp PTP grandmaster  5353/udp mDNS
# plus ephemeral UDP for RTP, RTCP/control, retransmits and NTP timing.
EXPOSE 8088/tcp 7000/tcp 319/udp 320/udp 5353/udp

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8088/api/state >/dev/null || exit 1

ENTRYPOINT ["/app/Streamsemble.Host"]
