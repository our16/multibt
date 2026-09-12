# driver/ — MultiBT Virtual Speaker

A vendored fork of **[Virtual Audio Driver](https://github.com/VirtualDrivers/Virtual-Audio-Driver)**, cut
down to a single minimal render endpoint.

> **STATUS: vendored, NOT YET BUILT.** See "Blocker" below — the build toolchain is incomplete on this
> machine, so nothing here has been compiled, signed, installed or verified. Treat every claim in this
> file that is not marked *verified* as a plan, not a result.

---

## Why this exists

Windows renders system audio **directly** to the default output endpoint. A WASAPI loopback capture is a
copy taken off that path, so the default device always plays its audio natively — with no added delay and
no volume control available to us. That is why a "primary" device could never be delayed: it was not being
skipped, it was on a different path that does not pass through MultiBT.

A virtual render endpoint fixes it by giving Windows an inaudible place to render into:

```text
All Windows audio
        |
        v
MultiBT Virtual Speaker          <- this driver (inaudible render endpoint)
        |
        | WASAPI loopback capture
        v
MultiBT Engine  (user mode)
  ring buffer / resampler / delay / drift control
        |
  +-----+-----+-----+
  v     v     v     v
 JBL  Sony  XGIMI  HDMI
```

Every audible speaker then becomes one of our **outputs**, and therefore becomes delayable and
volume-controllable — including whichever one the user thinks of as "primary". This is the same
architecture Voicemeeter uses, which is why Voicemeeter has to install a driver. Vendoring it removes the
third-party dependency (VB-CABLE / Voicemeeter).

**Today MultiBT already works with any third-party virtual cable** — `VirtualCableDetector` recognises
VB-CABLE, VoiceMeeter and Virtual Audio Cable, and the app can point Windows at one. This directory is the
replacement for that dependency, not a prerequisite for it.

---

## Blocker: the WDK is not installed

*Verified on this machine:*

| Component | State |
|---|---|
| Visual Studio Community 2022 | present |
| Visual Studio Build Tools 2026 | present |
| Windows SDK 10.0.26100.0 (`Include`, `bin`) | present |
| **Windows Driver Kit** (`Windows Kits\10\build`, `WindowsDriver.Default.props`) | **MISSING** |
| WDK uninstall entry | none |

A kernel driver cannot be compiled without the WDK: the `.vcxproj` files import
`WindowsDriver.Default.props` / `.targets`, which ship with it and not with the SDK. `build.bat` will fail
even though MSBuild exists.

**Install (one of):**

```powershell
# Option A - Visual Studio Installer: add the "Windows Driver Kit" individual component
# Option B - standalone WDK MSI, matching the installed SDK (10.0.26100)
#            https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk
```

Install with elevation, then `driver/build.bat release x64`.

---

## The build is only half of it: signing decides whether it can be installed at all

This is the part that most often derails a driver project *after* it compiles.

| Route | What it costs |
|---|---|
| **Test signing** (`bcdedit /set testsigning on` + reboot) | Free. Puts the machine in a test mode, shows a watermark, and is **not** something to ship to users. Fine for development. |
| **Attestation signing** (Microsoft Hardware Dev Center) | Requires an EV code-signing certificate and a Partner Center account. Signs for Windows 10/11 x64. This is the realistic path to "the installer ships the driver". |
| **WHQL / full signing** | Not needed here; no benefit for a render-only device. |

Upstream's README states the release is beta and needs test signing, so **treat this fork as a development
base, not a shippable driver.** "Install and uninstall the driver with the app" requires attestation
signing first — without it, the installer can only offer to enable test signing (admin + reboot).

---

## M0 change list (exact, derived from the vendored source)

*Verified by reading the files below; nothing here is guessed.*

The INF source is `Source/Main/VirtualAudioDriver.inx` — **not** a `.inf` in `Package/`. The `.inf` is
generated at build time by StampInf, and `Package/package.VcxProj` produces the `.cat`.

**Rename (device identity and binaries):**

| Where | Now | Target |
|---|---|---|
| `Source/Main/VirtualAudioDriver.inx` | `ROOT\VirtualAudioDriver` | `ROOT\MultiBTVirtualSpeaker` |
| `Source/Main/VirtualAudioDriver.inx` | `virtualaudiodriver.sys` | `MultiBTVirtualSpeaker.sys` |
| `Source/Main/VirtualAudioDriver.inx` | `CatalogFile = VirtualAudioDriver.cat` | `MultiBTVirtualSpeaker.cat` |
| `Source/Main/VirtualAudioDriver.inx` | `%VIRTUALAUDIODRIVER_SA.DeviceDesc%`, `%VIRTUALAUDIODRIVER.WaveSpeaker.szPname%` | `%MULTIBT...%` strings, set to `MultiBT Virtual Speaker` in `[Strings]` |
| `Source/Main/VirtualAudioDriver.inx` | `Provider = %ProviderName%` | MultiBT's provider |
| `Source/Main/VirtualAudioDriver.rc` | version / product strings | MultiBT |
| `VirtualAudioDriver.sln`, all `.vcxproj` | project + output names | `MultiBTVirtualSpeaker` |

**Strip the microphone — this is where most of the complexity lives:**

The .inx declares **two** interfaces and we only want one:

* KEEP — `VIRTUALAUDIODRIVER.I.WaveSpeaker` and `VIRTUALAUDIODRIVER.I.TopologySpeaker`
  (`HKR,,FriendlyName,,%VIRTUALAUDIODRIVER.WaveSpeaker.szPname%`)
* DELETE — `VIRTUALAUDIODRIVER.I.WaveMicArray1` and `VIRTUALAUDIODRIVER.I.TopologyMicArray1`
  (comment in the .inx reads `capture interfaces: mic array (internal: front)`)

Removing those alone is not enough: the microphone also appears in the topology and in the miniport's
endpoint/pin tables. Expect to touch, at minimum:

* `Source/Inc/endpoints.h`, `Source/Inc/basetopo.h`, `Source/Inc/definitions.h` — endpoint/pin descriptors
* `Source/Main/basetopo.cpp`, `Source/Main/mintopo.cpp`, `Source/Main/adapter.cpp` — topology and
  subdevice construction
* `Source/Filters/speakertopo.cpp`, `speakertoptable.h`, `speakerwavtable.h` — keep
* `Source/Filters/micarraytopo.cpp`, `micarray1toptable.h`, `micarraywavtable.h`, `minipairs.h` — remove
* `Source/Utilities/ToneGenerator.*` — upstream test-tone helper; remove unless the smoke test needs it

**Do this in one small step at a time.** The real risk with kernel audio drivers is not writing the code —
it is changing several layers at once and then being unable to tell which one caused a crash, silence,
glitching or a device that refuses to enumerate. Rename first, build, install, confirm it enumerates, and
only then remove the microphone.

---

## Milestones

Each is independently verifiable; do not start the next before the previous is green.

| # | Goal | Verified by |
|---|---|---|
| **M0** | Installs and enumerates | `Get-PnpDevice` lists `MultiBT Virtual Speaker`; it appears in Sound settings; survives a reboot; uninstall removes it completely |
| **M1** | Passes PCM to user mode | Set it as default, play audio, and confirm MultiBT captures it — **output to a wired device only; no Bluetooth, no sync, no delay** |
| **M2** | Multi-device output | The existing `AudioEngine` fans out to 3 speakers at once |
| **M3** | Per-device delay + volume | Already implemented in the engine and app; this milestone is only about the driver feeding it |
| **M4** | Drift compensation | Already implemented (`DriftController`); confirm it holds over an hour |
| **M5** | Acoustic latency detection | Not implemented end-to-end; the signal and gates exist (`SweepGenerator`) |

M1 is the milestone that matters. Prove **Windows → driver → MultiBT → one real device** is stable, with no
glitches and no persistent underruns, before adding anything else.

---

## Architectural rule: the driver stays minimal

```text
Windows Audio Engine -> MultiBT Virtual Speaker -> PCM -> MultiBT Engine (user mode)
```

The driver's entire job is: **register a render endpoint, receive PCM, expose it stably.**

It must NOT contain: JSON, HTTP, Bluetooth, GUI, profiles, device discovery, latency calculation,
synchronisation, resampling, or drift control. All of that is user-mode, where a bug is a crash rather than
a bugcheck.

Corollary: **the driver does not call `MultiBT.exe`.** It receives audio the way any audio device does, and
user mode captures it through the same WASAPI loopback path the app already uses. That keeps the kernel
surface small and means M1 needs no new IPC at all — which is why M1 is cheap to reach and worth reaching
first.

### Audio format

Prefer stereo 48 kHz, and accept 16/24/32-bit. Normalise to **48 kHz stereo float** inside the user-mode
engine, not in the driver — the engine already does rate and channel adaptation per output device.

---

## Licence obligations

Upstream is MIT for its own code and descends from Microsoft's SysVAD sample. `LICENSE` and
`THIRD_PARTY_NOTICES.md` are preserved verbatim in this directory — **keep both**, and keep the Microsoft
attribution, since the miniport/topology code originates from the Windows Driver Samples.

---

## Layout

```text
driver/
├── Source/                 # driver sources (Main / Filters / Inc / Utilities)
│   └── Main/VirtualAudioDriver.inx   # the INF source to rename
├── Package/package.VcxProj # produces the .cat
├── VirtualAudioDriver.sln
├── build.bat               # searches for MSBuild under VS 2022 paths
├── LICENSE                 # upstream MIT
├── THIRD_PARTY_NOTICES.md  # Microsoft SysVAD attribution
├── UPSTREAM-README.md      # upstream's own README, kept for reference
└── README.md               # this file
```

Our application code is unchanged by any of this: the driver is an optional accelerator for an
architecture the app already supports via third-party cables.
