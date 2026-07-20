# Streamsemble

A cross-platform (.NET 8) audio hub that receives from **Spotify Connect**,
**AirPlay 2**, and **Google Cast** sources and plays out to **one or more
AirPlay 2 speakers**, kept in sync. Built with the .NET Generic Host and
dependency injection throughout.

```
 Spotify Connect  ┐
 AirPlay 2 (recv) ┼─► arbiter ─► pump ─► AirPlayTargetGroup ─► speaker A
 Google Cast      ┘   (last-writer                          └─► speaker B
      ▲               -wins)                                       …
      │                 │                         ▲
 iPhone / Mac           └── ONE grandmaster ──────┘  PTP (319/320) + NTP timing
 streams into the hub       clock (PtpPortMux)       + sync packets = multi-room
```

## What works today (verified)

| Capability | Status | How it was verified |
|---|---|---|
| **AirPlay 2 receiver → hub → N speakers** | ✅ Working | iPhone picks "Streamsemble Hub" → hub decodes and fans out to three real speakers (2× Sonos + Philips Google TV) simultaneously; every `SETRATEANCHORTIME` accepted first try. |
| **AirPlay 2 receiver, realtime (type 96, ALAC/UDP)** | ✅ Working | Real Mac (system output) and iPhone streamed into the receiver; ALAC → PCM → WAV at exactly 176.4 KB/s, zero decrypt/decode/lost packets. This is what the iOS/macOS *picker* always sends. |
| **AirPlay 2 receiver, buffered (type 103, AAC/TCP)** | ✅ Working (loopback) | Own sender → receiver loopback: 111.8 s of 440 Hz recovered bit-clean (RMS 4632 vs 4634 theoretical). Music-app end-to-end still pending a live test. |
| **HAP transient pairing server + FairPlay fp-setup** | ✅ Working | Real Mac and iPhone complete SRP pair-setup against `HapSrpServer` (BouncyCastle's SRP is wire-incompatible — never use it) and the canned-table FairPlay responder; sessions upgrade to the encrypted channel. |
| **One hub grandmaster clock (PTP)** | ✅ Working | `PtpPortMux` owns UDP 319/320; `PtpReceiverClock` serves inbound senders (priority 248, TV-exact wire shape) *and* the speaker group (priority 247) from one clock id. Macs/iPhones demonstrably slave to it (continuous Delay_Req), and all speaker anchors validate on its timeline. |
| **Spotify Connect source** | ✅ Working | librespot child process; the device "Streamsemble" appears in the Spotify app and streams S16LE PCM into the pipeline. |
| **AirPlay 2 / RAOP sender → 1 speaker** | ✅ Working | Streamed a 440 Hz tone to shairport-sync; recovered a clean 440 Hz tone at the receiver. |
| **Sender fan-out → N speakers, in sync** | ✅ Working | Real-device group (TV + Sonos) natively synced ≲25 ms with zero manual trims; earlier shairport-sync pair showed ~1.8 ms content skew by cross-correlation. Late joiners map onto the group's epoch anchor (`GroupTimelineAnchor`) instead of anchoring at "now", so a speaker joining mid-stream lands on the house timeline. |
| **Retransmit / packet ring** | ✅ Working | Control channel answers RAOP resend requests from a shared plain-packet ring, re-framed per target. |
| **ALAC decoder (Apple reference port)** | ✅ Working, unit-tested | SCE/CPE/verbatim/partial frames, all three magic-cookie forms; byte-identical against ffmpeg-generated golden vectors. |
| **Volume & metadata forwarding** | ✅ Working | Spotify volume/track events → RAOP `SET_PARAMETER` (dB volume, DMAP metadata). Per-speaker volume is also read back from the device (`GET_PARAMETER`, on connect + periodic) and settable per speaker from the web UI. Inbound sender volume is observe-only by design. |
| **Source arbitration** | ✅ Working | Last source to play wins; the previous source is asked to yield. Unit-tested. |
| **Web UI: discovery, selection, volume, sync telemetry** | ✅ Working | Browser at `http://<host>:8088` lists discovered AirPlay speakers; ticking one connects it live (mid-stream join), unticking drops it. Per-speaker + averaged global volume, live sync/latency graphic, and a technical panel (anchor state, buffer lead, PTP lock, epoch). |
| **Google Cast source** | ⚠️ Stub (by design) | See limitation below. |

## Protocol notes (hard-won, save yourself the week)

- **The receiver cannot choose the stream type.** macOS/iOS system output
  (the picker) computes ALAC *realtime* for every audio-class receiver —
  including a real Sonos — before ever connecting; Music-class senders use
  *buffered*. No TXT record or `/info` shape flips it, so a receiver must
  accept type 96 to be usable from the picker. (Observed in
  `AirPlayXPCHelper` engine logs, 2026-07-19: `engine RTAudio, audioFormat
  0x40000` even when targeting the Sonos; see `ReceiverFeatures.cs`. The
  Mac→Sonos/TV *buffered* pcaps in `debug/` are Music-app sessions.)
- **Modern senders bind audio transport only via `streamConnections`.** The
  AirPlay 3.x-sdk stream `SETUP` carries `streamConnections` /
  `streamConnectionID`; the reply must mirror them with our RTP/RTCP ports.
  The legacy `dataPort`/`controlPort` keys are ignored — without the mirror
  the sender's UDP channels die unbound ~10 ms after creation, with the
  session otherwise healthy and **no error logged anywhere on either side**.
- **Advertise routable IPv4 only.** mDNS AAAA records with a link-local v6
  get resolved first by macOS; TCP survives but NW-UDP silently refuses to
  dial a bare `fe80::` — audio goes nowhere.
- **Never discipline the group to a speaker's clock.** Both Sonos and
  Google-TV-class receivers are grandmaster-capable announcers (priority
  248); whichever wins plays and every other speaker rejects anchors on a
  timeline it can't see. The hub must *be* the grandmaster for everything —
  and must announce to speakers at priority 247, since a 248 tie falls to
  clock-identity comparison you can lose.
- **Nothing may hold UDP 319/320 on a machine that *sends* picker-AirPlay** —
  macOS aborts its own sessions instantly if the ports are taken. Fine on a
  dedicated hub box; check `lsof -nP -iUDP:319 -iUDP:320` when debugging.
- Debugging on macOS: `/usr/bin/log show --predicate 'process ==
  "AirPlayXPCHelper"'` shows the sender's engine/transport decisions
  (activation options, chosen `AudioEngineType`, per-connection dials).

## Deliberately incomplete

- **AirPlay 2 HAP sender for HomePods (M4) — built, needs a real HomePod to
  finish.** A target with `Protocol: AirPlay2` runs the full HAP path
  (encrypted RTSP, stream SETUP, per-packet ChaCha20), matched
  constant-for-constant to pair_ap/OwnTone and unit-tested. Pairing is
  transient SRP by default, or **verified HomeKit PIN pairing** with a
  persisted identity (`STREAMSEMBLE_PAIR=1`, PIN via
  `~/.streamsemble/pin.txt`) — the path TV-class receivers require before
  they render. What remains is wire verification against actual HomePod
  firmware. Sonos/TV-class AirPlay 2 speakers and RAOP remain the verified
  paths.
- **Google Cast playback.** Not implementable against official sender apps: a
  Cast receiver must complete CastV2 DeviceAuth with a **Google-CA-signed device
  certificate**, which senders verify. An emulated receiver is discovered but
  always rejected. `ICastSource` keeps the slot wired for any future licensed
  path.
- **Receiver odds and ends:** Music-app (buffered) inbound not yet tested
  end-to-end against a real Mac; multi-speaker sync *tightness* with a phone
  source not yet measured (the chirp-calibration rig exists); persistent
  HomeKit pairing not offered (transient PIN pairing only).

## Project layout

| Project | Role |
|---|---|
| `Streamsemble.Core` | Format, `PcmFrame`, ring buffer, `IAudioSource`/`IAudioSink`/`ISourceArbiter`, arbiter, pump, WAV/null/tone sinks & sources. Zero protocol deps. |
| `Streamsemble.Timing` | `IMasterClock`, NTP timing responder, and the PTP stack: `PtpPortMux` (single owner of 319/320), `PtpReceiverClock` (the hub grandmaster), wire builders pinned by tests. |
| `Streamsemble.Discovery` | mDNS browse and advertise (routable-IPv4-only records). |
| `Streamsemble.AirPlay.Common` | Shared RTSP/plist/TLV8 + HAP pairing crypto, client and server side (`HapSrpServer`, transient pair-setup, FairPlay responder). |
| `Streamsemble.AirPlay.Sender` | RAOP + AirPlay 2 sessions, RTP send, control/sync channel, `AirPlayTargetGroup` fan-out sink. |
| `Streamsemble.AirPlay.Receiver` | Full receiver: RTSP server, session handling, realtime (ALAC) + buffered (AAC) audio servers, receiver source. |
| `Streamsemble.Spotify` | Supervised librespot child process → PCM + events. |
| `Streamsemble.Cast.Stub` | `ICastSource` stub. |
| `Streamsemble.Host` | Console app: Generic Host, DI wiring, config. |

## Web UI

Run the host and open **`http://localhost:8088`** (also reachable from a phone
on the same LAN at `http://<host-ip>:8088`). The page:

- lists AirPlay speakers **discovered live via mDNS** (`_raop._tcp` +
  `_airplay._tcp`, so AirPlay-2-only devices like TVs appear too),
- lets you **tick speakers to play to them** — selection is applied to the
  running fan-out group immediately, so a speaker can join or leave *while audio
  is playing*, staying in sync via the shared clock,
- shows **per-speaker volume sliders** fed by the device's own reported volume
  (read-only `GET_PARAMETER` on connect + periodic refresh, so changes made
  from the speaker's own app show up); the global slider reports the **mean of
  the live speaker volumes** and sets all speakers when moved,
- shows the active source and now-playing metadata plus the live pipeline
  state (playing / paused-with-pipeline-held / stopped, send-queue depth,
  anchored count, packet rate),
- draws a **sync & latency graphic**: per-speaker render-head lead vs the
  group presentation-latency target with sparkline history, lock/cushion
  badges, and a hover tooltip (encoder pipeline age, inherited join debt,
  last PTP `Delay_Req`),
- ends with a **technical details panel**: group epoch anchor, grandmaster
  clock id, and a per-session table (mode, pairing, anchor state, trims,
  reported latency, timeline id).

The REST API behind it (usable directly): `GET /api/state` (now includes
`speakers[]` per-session telemetry and a group `telemetry` object),
`POST /api/targets` (`{ "targets": [ { "name": "Living Room" } ] }`),
`POST /api/volume` (`{ "volume": 0.7 }`, all speakers), and
`POST /api/speakers/volume` (`{ "name": "Kitchen", "volume": 0.4 }`, one
speaker). Configured `AirPlaySender:Targets` seed the initial selection, so
headless/config-only operation still works.

## Running

Prerequisites: **.NET 8 SDK**, and **librespot** for the Spotify source
(`brew install librespot`, found on `PATH` by default; point
`Spotify:LibrespotPath` at a custom binary). For unattended running, build the
resilience fork (`tools/librespot-resilience.patch`) — stock librespot exits
when Spotify's server drops the session; the fork re-announces and reclaims
playback.

```bash
# The full hub: AirPlay in (pick "Streamsemble Hub" on your phone) → speakers out.
# PTP ports 319/320: macOS allows binding them unprivileged; Linux needs
# root or CAP_NET_BIND_SERVICE.
dotnet run --project src/Streamsemble.Host -- \
  --Streamsemble:Sink=AirPlay \
  --AirPlayReceiver:Enabled=true --AirPlayReceiver:Name="Streamsemble Hub" \
  --AirPlaySender:Targets:0:Name="Kitchen"     --AirPlaySender:Targets:0:Protocol=AirPlay2 --AirPlaySender:Targets:0:StreamMode=Buffered \
  --AirPlaySender:Targets:1:Name="Living room TV" --AirPlaySender:Targets:1:Protocol=AirPlay2 --AirPlaySender:Targets:1:StreamMode=Buffered

# Spotify → two AirPlay speakers, in sync (resolve by name via mDNS):
dotnet run --project src/Streamsemble.Host -- \
  --Streamsemble:Sink=AirPlay \
  --AirPlaySender:Targets:0:Name="Living Room" \
  --AirPlaySender:Targets:1:Name="Kitchen"

# AirPlay in → record to WAV (no speakers needed):
dotnet run --project src/Streamsemble.Host -- \
  --Streamsemble:Sink=Wav --AirPlayReceiver:Enabled=true --Spotify:Enabled=false

# Test tone (click track) → speaker, no Spotify:
dotnet run --project src/Streamsemble.Host -- \
  --Streamsemble:TestTone=true --Streamsemble:Sink=AirPlay --Spotify:Enabled=false \
  --AirPlaySender:Targets:0:Host=192.168.1.50
```

For Spotify, open the app and pick **Streamsemble** as the playback device.

> ⚠️ Don't run the receiver on a machine you also AirPlay *from* via the
> system picker: the hub's PTP clock owns UDP 319/320, and macOS aborts its
> own outbound AirPlay sessions when anything holds those ports. Streaming
> from *other* devices (phones, other Macs) to a hub on this machine is fine.

### Configuration (`src/Streamsemble.Host/appsettings.json`)

- `Streamsemble:DeviceName` — advertised name.
- `Streamsemble:Sink` — `AirPlay` | `Wav` | `Null`.
- `AirPlaySender:Targets[]` — `{ Name | Host, Port, Protocol, StreamMode, Encryption, LatencyTrimMs }`.
  `StreamMode: Buffered` is the verified mode for AirPlay 2 speakers.
  `LatencyTrimMs` manually trims one speaker's alignment if a vendor's reported
  latency is off.
- `AirPlayReceiver:Enabled`, `AirPlayReceiver:Name` — the inbound AirPlay hub.
- `Spotify:Enabled`, `Spotify:LibrespotPath`, `Spotify:Bitrate`,
  `Spotify:ExtraArgs` (passed through to librespot, e.g. `--volume-ctrl fixed`).
- Verified HomeKit pairing for strict receivers (TVs): set
  `STREAMSEMBLE_PAIR=1`, then write the on-screen PIN to
  `~/.streamsemble/pin.txt` when prompted; the identity persists for future
  connects.

## Testing

```bash
dotnet test    # 56 tests (AirPlay + core): SRP client↔server interop, ALAC
               # decoder vs ffmpeg golden vectors, PTP grandmaster wire pins,
               # ring buffer, arbiter, packers
```

Multi-room sync was verified end-to-end on real devices (TV + Sonos,
≲25 ms native alignment) and earlier with two local shairport-sync receivers
and FFT cross-correlation (~1.8 ms).

## Linux deployment notes

Everything is cross-platform; the receiver has run on Ubuntu 24.04 arm64
(`dotnet publish -r linux-arm64 --self-contained`). The PTP clock needs
ports 319/320 (`setcap CAP_NET_BIND_SERVICE` or root) and conflicts with any
nqptp/shairport-sync on the same host. The librespot event helper uses
`curl` (present on typical Linux hosts).
