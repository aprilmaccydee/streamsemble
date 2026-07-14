# Streamsemble

A cross-platform (.NET 8) audio hub that receives from **Spotify Connect**,
**AirPlay 2**, and **Google Cast** sources and plays out to **one or more
AirPlay 2 speakers**, kept in sync. Built with the .NET Generic Host and
dependency injection throughout.

```
 Spotify Connect ┐
 AirPlay 2 (recv) ┼─► arbiter ─► pump ─► AirPlayTargetGroup ─► speaker A
 Google Cast     ┘   (last-writer                          └─► speaker B
                      -wins)                                      …
                        │                         ▲
                        └── one master clock ─────┘  NTP timing + per-second
                            (shared timeline)        sync packets = multi-room sync
```

## What works today (verified)

| Capability | Status | How it was verified |
|---|---|---|
| **Spotify Connect source** | ✅ Working | librespot child process; the device "Streamsemble" appears in the Spotify app and streams S16LE PCM into the pipeline. |
| **AirPlay 2 / RAOP sender → 1 speaker** | ✅ Working | Streamed a 440 Hz tone to shairport-sync; recovered a clean 440 Hz tone at the receiver. |
| **Sender fan-out → N speakers, in sync** | ✅ Working | Two shairport-sync receivers; cross-correlation of their outputs showed **~1.8 ms** content skew — sub-audible, ~1 ms class. |
| **Shared master clock + NTP timing responder + sync packets** | ✅ Working | The sender answers each speaker's timing queries and sends per-second sync packets from one clock; this is what holds the fan-out together. |
| **Retransmit / packet ring** | ✅ Working | Control channel answers RAOP resend requests from a shared plain-packet ring, re-framed per target. |
| **HAP pairing crypto (transient SRP, X25519, Ed25519, ChaCha20, HKDF, TLV8)** | ✅ Working, unit-tested | 18 AirPlay tests incl. full SRP client↔server interop and the encrypted-channel/audio key derivation, matched to pair_ap constants. |
| **AirPlay 2 HAP sender → HomePod** | ⚙️ Implemented, needs device test | Transient pair-setup, encrypted RTSP channel, realtime stream SETUP, and ChaCha20 audio packets are built to the pair_ap/OwnTone spec. Not yet run against real HomePod firmware — see below. |
| **Volume & metadata forwarding** | ✅ Working | Spotify volume/track events → RAOP `SET_PARAMETER` (dB volume, DMAP metadata). |
| **Source arbitration** | ✅ Working | Last source to play wins; the previous source is asked to yield. Unit-tested. |
| **DI / Generic Host / options / logging** | ✅ Working | Every component is constructor-injected; config via `appsettings.json` + command line. |
| **Web UI: mDNS discovery + live speaker selection** | ✅ Working | Browser at `http://<host>:8088` lists discovered AirPlay speakers; ticking one connects it live (mid-stream join), unticking drops it; volume + now-playing shown. Verified end-to-end against shairport-sync. |
| **AirPlay receiver mDNS advertisement** | ✅ Working | Hub appears under `_airplay._tcp` / `_raop._tcp` (`dns-sd -B`). |
| **Google Cast source** | ⚠️ Stub (by design) | See limitation below. |

## Deliberately incomplete

- **AirPlay 2 *receiver* audio path (M3).** The receiver advertises and the
  source slot is wired, but inbound **pairing (SRP transient pair-setup,
  pair-verify), fp-setup (FairPlay), PTP timing, and the buffered-audio
  decrypt/AAC-decode path are not implemented.** Disabled by default
  (`AirPlayReceiver:Enabled=false`) so iOS doesn't show a device that can't
  finish connecting. Reference implementations to port from: goplay2 and
  shairport-sync.
- **AirPlay 2 HAP sender for HomePods (M4) — built, needs a real HomePod to
  finish.** A target with `Protocol: AirPlay2` now runs the full HAP path:
  transient SRP pair-setup, HKDF-derived encrypted RTSP control channel
  (`HapCipherStream`), realtime stream SETUP (type 96, `shk` audio key), and
  per-packet ChaCha20-Poly1305 audio (`AirPlay2AudioCipher`) — all matched
  constant-for-constant to pair_ap/OwnTone and unit-tested for internal
  correctness. What remains is wire verification against actual HomePod
  firmware: the SETUP plist field set is the most likely thing to need tuning
  (Apple ignores/rejects unexpected keys), and there is no HomePod in the dev
  environment to iterate against. RAOP (`Protocol: Auto`/`Raop`) remains the
  verified path for third-party AirPlay 2 speakers. Reference: OwnTone
  `outputs/airplay.c` + pair_ap.
- **Google Cast playback.** Not implementable against official sender apps: a
  Cast receiver must complete CastV2 DeviceAuth with a **Google-CA-signed device
  certificate**, which senders verify. An emulated receiver is discovered but
  always rejected. `ICastSource` keeps the slot wired for any future licensed
  path.

## Project layout

| Project | Role |
|---|---|
| `Streamsemble.Core` | Format, `PcmFrame`, ring buffer, `IAudioSource`/`IAudioSink`/`ISourceArbiter`, arbiter, pump, WAV/null/tone sinks & sources. Zero protocol deps. |
| `Streamsemble.Timing` | `IMasterClock` and the NTP timing responder — the sync foundation. |
| `Streamsemble.Discovery` | mDNS browse (`_raop._tcp`) and advertise. |
| `Streamsemble.AirPlay.Common` | (M3/M4) shared RTSP/plist/TLV8 + HAP pairing crypto. |
| `Streamsemble.AirPlay.Sender` | RAOP session, RTP send, control/sync channel, `AirPlayTargetGroup` fan-out sink. |
| `Streamsemble.AirPlay.Receiver` | Receiver source + mDNS advertisement (scaffolding). |
| `Streamsemble.Spotify` | Supervised librespot child process → PCM + events. |
| `Streamsemble.Cast.Stub` | `ICastSource` stub. |
| `Streamsemble.Host` | Console app: Generic Host, DI wiring, config. |

## Web UI

Run the host and open **`http://localhost:8088`** (also reachable from a phone
on the same LAN at `http://<host-ip>:8088`). The page:

- lists AirPlay speakers **discovered live via mDNS** (`_raop._tcp`),
- lets you **tick speakers to play to them** — selection is applied to the
  running fan-out group immediately, so a speaker can join or leave *while audio
  is playing*, staying in sync via the shared clock,
- shows the active source, now-playing metadata, and a master volume slider.

The REST API behind it (usable directly): `GET /api/state`,
`POST /api/targets` (`{ "targets": [ { "name": "Living Room" } ] }`),
`POST /api/volume` (`{ "volume": 0.7 }`). Configured `AirPlaySender:Targets`
seed the initial selection, so headless/config-only operation still works.

## Running

Prerequisites: **.NET 8 SDK**, and **librespot** on `PATH` for the Spotify
source (`brew install librespot`).

```bash
# Spotify → record to WAV (no speakers needed):
dotnet run --project src/Streamsemble.Host -- \
  --Streamsemble:Sink=Wav

# Spotify → two AirPlay speakers, in sync (resolve by name via mDNS):
dotnet run --project src/Streamsemble.Host -- \
  --Streamsemble:Sink=AirPlay \
  --AirPlaySender:Targets:0:Name="Living Room" \
  --AirPlaySender:Targets:1:Name="Kitchen"

# Test tone (click track) → speaker, no Spotify:
dotnet run --project src/Streamsemble.Host -- \
  --Streamsemble:TestTone=true --Streamsemble:Sink=AirPlay --Spotify:Enabled=false \
  --AirPlaySender:Targets:0:Host=192.168.1.50
```

Then open Spotify and pick **Streamsemble** as the playback device.

### Configuration (`src/Streamsemble.Host/appsettings.json`)

- `Streamsemble:DeviceName` — advertised name.
- `Streamsemble:Sink` — `AirPlay` | `Wav` | `Null`.
- `AirPlaySender:Targets[]` — `{ Name | Host, Port, Protocol, Encryption, LatencyTrimMs }`.
  `LatencyTrimMs` manually trims one speaker's alignment if a vendor's reported
  latency is off.
- `Spotify:Enabled`, `Spotify:LibrespotPath`, `Spotify:Bitrate`.
- `AirPlayReceiver:Enabled` — leave `false` until M3.

## Testing

```bash
dotnet test          # ring buffer, arbiter, ALAC packer
```

The multi-room sync was verified end-to-end with two local shairport-sync
receivers (one bound to IPv4, one to IPv6 on port 5000) and an FFT
cross-correlation of their captured output; see the "What works" table.

## Linux deployment notes

Everything is cross-platform. The librespot event helper uses `curl`
(present on typical Linux hosts). A future AirPlay 2 receiver's PTP responder
needs ports 319/320 (`setcap CAP_NET_BIND_SERVICE` or root) and conflicts with
any nqptp/shairport-sync on the same host.
