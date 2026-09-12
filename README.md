# MultiBT

**English** · [中文](README.zh-CN.md)

> **The "multi-output device + automatic sync" button Windows never shipped.**
> Not an audio workstation, not a Voicemeeter clone, no kernel driver.

Play one system audio stream through several output devices at once — a Bluetooth speaker, a USB DAC,
an HDMI projector — each with its own volume and delay, while MultiBT keeps correcting their clock
drift **during playback** so they do not slowly slide apart.

Configure it once, and it comes back by itself after a reboot. **You should not need to open the main
window day to day.**

```text
                Windows Audio
                     |
                     v
              MultiBT engine
        +------------+------------+
        v            v            v
   Bluetooth       USB          HDMI
   + 120 ms      + 40 ms       + 0 ms
```

## Why it exists

Windows can already send one stream to several devices. What is missing is the part everyone actually
needs:

* every device plays at a **slightly different latency**, so they do not sound like one system;
* a **Bluetooth** device is 150-400 ms behind a wired one, which no amount of "just pick two outputs"
  will fix;
* devices **drift** against each other over minutes, so a set-up that starts in sync slowly smears;
* and the default device **cannot be delayed or volume-controlled at all**, because Windows plays to it
  directly rather than through the app.

MultiBT exists to solve exactly those four things, and to stay out of the way afterwards.

## What it does

* **One stream, many speakers** — any number of output endpoints, opened at once.
* **Per-device delay** — 0-2000 ms, applied as a real delay line, not a buffer guess.
* **Per-device volume** — drives each device's *actual* Windows volume, so 100 % means 100 % of that
  device, and the app shows you what the device really is.
* **Continuous drift correction** — a 5 Hz control loop measures each device's buffer and trims it with
  a variable-rate resampler, so outputs stay aligned for hours instead of minutes.
* **Settings you never have to save** — everything is persisted as you change it, with **Ctrl+Z /
  Ctrl+Y** to step back and forth through your changes.
* **Survives device churn** — hot-plug detection; a Bluetooth speaker that reconnects is picked up and
  re-attached automatically.
* **Profiles** — a named set of devices and levels, for switching between e.g. "living room" and "desk".
* **Honest numbers** — the app reports the system's end-to-end latency and warns when it exceeds the
  point where video lip-sync visibly breaks.

## Requirements

| | |
| --- | --- |
| OS | Windows 10 1903+ or Windows 11, x64 |
| Runtime | **None** — the published build is self-contained |
| Virtual audio device | **Optional**, see below |

## Getting started

1. Download `MultiBT-Preview-win-x64.zip`, unzip, run `MultiBT.App.exe`.
2. Tick the output devices you want in the list.
3. Check **Audio input** — `Auto` follows your Windows default output, which is the right choice most of
   the time.
4. Press **Start mirroring**.

### Do I need a virtual cable?

**No.** MultiBT captures whatever your Windows default output is playing, so it works out of the box and
needs nothing installed.

Installing a virtual cable (VB-CABLE, VoiceMeeter, Virtual Audio Cable...) makes it *better*, and only in
one specific way: Windows then renders into a device nobody hears, so **every** speaker becomes one of
MultiBT's outputs — including the one that used to be the default. Without a cable, that one device plays
natively, which means MultiBT cannot delay or volume-control it.

The **Get a virtual cable** button in the app lists a few options and opens the vendor's own page.
**MultiBT recommends; it does not bundle or download anything.** See
[docs/DECISIONS.md](docs/DECISIONS.md) for why.

## Known limitations

Stated plainly, because a README that only lists strengths is not useful:

* **Aligning to a Bluetooth speaker costs latency.** Everything is delayed to match the slowest device,
  which can push the system past ~125 ms and make video look out of sync. The **WiredOnly** sync mode
  exists for watching video.
* **Acoustic latency is not measured.** There is no microphone-based calibration end to end, so automatic
  alignment is based on a **stated estimate per transport** (Bluetooth 200 ms, HDMI 20 ms, wired 10 ms).
  The manual offset absorbs whatever the estimate gets wrong.
* **This is a preview.** The audio path has not been verified by ear on real hardware yet.
* Speaker sensitivity differences cannot be corrected automatically; only Windows volumes can.

## Documentation

| | |
| --- | --- |
| [docs/SPEC.md](docs/SPEC.md) | What the product must do, and the algorithms |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Why it is built this way — including why there is no driver |
| [docs/PITFALLS.md](docs/PITFALLS.md) | The Windows/audio traps, and what they cost |
| [PROGRESS.md](PROGRESS.md) | What has been verified, and what has not |
| [docs/DEVELOPING.md](docs/DEVELOPING.md) | Building, testing, project layout |

## Licence

**MIT** — see [LICENSE](LICENSE). All dependencies are MIT as well (NAudio, H.NotifyIcon.Wpf), so there
is no copyleft obligation to inherit.

MultiBT ships **no** audio driver and redistributes nobody's installer.