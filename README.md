# ROCm Real-Time Vocal Splitter (.NET 10)

> **Real-time, hardware-accelerated AI vocal separation and dual-bus audio routing engine built in .NET 10 and AMD ROCm for live streaming and performance workflows.**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![AMD ROCm](https://img.shields.io/badge/AMD-ROCm%20%7C%20DirectML-ED1C24?logo=amd)](https://www.amd.com/en/products/software/rocm.html)
[![Avalonia UI](https://img.shields.io/badge/UI-Avalonia%2011-0D47A1)](https://avaloniaui.net/)
[![Version](https://img.shields.io/badge/Release-v1.0.0%20GA-10B981)]()

---

## 🎯 Project Objective

**ROCm Real-Time Vocal Splitter** enables live streamers, musicians, and content creators to separate vocals from any music source in real time—without leaving their broadcast software or dropping game framerates.

Using frequency-domain MDX-Net neural models (`UVR_MDXNET_KARA_2.onnx`, `UVR-MDX-NET-Inst_HQ_5.onnx`) accelerated via **AMD ROCm / HIP** (with automatic **DirectML** fallback), the engine splits live audio into two sample-accurate, phase-aligned output buses:

* **Bus 1 — Local Monitoring (Headphones):** Delivers the full original track (music + lead vocals) dynamically delayed to match the exact neural inference latency so performers hear natural accompaniment without echo or phase drift.
* **Bus 2 — Stream / Broadcast Output (Virtual Audio Cable):** Mixes the AI-isolated instrumental stem with the performer's live microphone (also delay-compensated to the exact sample) and routes it directly to **OBS Studio** or **Meld Studio**.

---

## ✨ Key Capabilities (v1.0.0 GA)

* **AMD ROCm & DirectML Acceleration:** Targets RDNA3 / RDNA4 GPUs (validated on AMD Radeon RX 9060 XT 16 GB VRAM) with a strict 2 GB VRAM arena cap so AI inference safely coexists with 3D games and hardware video encoders.
* **Per-Process WASAPI Audio Isolation:** Uses Windows 11 `ActivateAudioInterfaceAsync` (`PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`) to isolate a single application (e.g., Chrome, Spotify, Edge) while ignoring game audio and alerts—plus automatic feedback-loop protection.
* **Zero-Allocation Streaming DSP:** Lock-free pinned `AudioRingBuffer`, 75% Overlap-Add (OLA) STFT synthesis with periodic Hann windows, sample-accurate `SampleDelayLine` synchronization, and a soft-knee brickwall limiter.
* **Avalonia Desktop Mixer & 60 FPS Telemetry:** Real-time 5-channel peak VU meters, live dry/wet vocal suppression fader, manual sync offset ($\pm 50\text{ ms}$), and live GPU inference latency HUD.
* **Streamer Workflow Tools:** System-wide Win32 `RegisterHotKey` shortcuts (`Ctrl+Alt+V`, `Ctrl+Alt+M`, `Ctrl+Alt+S`, `Ctrl+Alt+1..3`), JSON preset profiles (`%AppData%\RocmAudioSplitter\profiles.json`), 440 Hz OBS routing calibration tone, and an automatic GPU overload Safe-Mode Bypass watchdog.

---

## 🏗️ Solution Architecture

```text
RocmAudioSplitter/
├── RocmAudioSplitter.Core/          # Zero-GC DSP engine, WASAPI capture, ROCm/ONNX host, Watchdog & Profiles
├── RocmAudioSplitter.Desktop/       # Avalonia 11 MVVM desktop dashboard, 60fps VU meters & Global Hotkeys
├── RocmAudioSplitter.Cli/           # CLI benchmarking & offline stem separation tool (WAV/MP3/FLAC)
├── publish-release.ps1              # Self-contained win-x64 release build script
└── installer.iss                    # Inno Setup Windows installer script
```

### Signal & Synchronization Pipeline

```text
[Target App / System Audio] ──► WASAPI Loopback ──┬──► SampleDelayLine ─────────────────────► [Bus 1: Headphones]
                                                  └──► OLA STFT ──► AMD ROCm ONNX ──┐
                                                                                    ├──► [Bus 2: Virtual Cable (OBS/Meld)]
[Live Microphone] ───────────► 80Hz HPF + Gain ──────► SampleDelayLine ─────────────┘
```

---

## 🚀 Getting Started

### Prerequisites

* **OS:** Windows 11 (64-bit, build 22000+ required for per-process audio loopback)
* **SDK:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* **GPU:** AMD Radeon GPU with ROCm 7.1+ runtime (`amdhip64_7.dll`) or any DirectX 12 GPU via DirectML fallback
* **Virtual Audio Routing:** [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (recommended for OBS Studio / Meld Studio routing)
* **ONNX Model:** Place `UVR_MDXNET_KARA_2.onnx` or `UVR-MDX-NET-Inst_HQ_5.onnx` in the application folder or `Downloads` directory (auto-detected on launch).

### Build & Run

```powershell
# Clone the repository
git clone https://github.com/brunosr1985/ROCmOk-.git
cd ./ROCmOk-/sources

# Build the solution in Release mode
dotnet build -c Release

# Launch the Desktop Application
dotnet run --project RocmAudioSplitter.Desktop -c Release
```

### Packaging a Standalone Release

```powershell
.\publish-release.ps1
# Output: .\publish\win-x64\RocmAudioSplitter.Desktop.exe
```

---

## ⌨️ Default Global Hotkeys

| Shortcut | Action |
| :--- | :--- |
| `Ctrl + Alt + V` | Toggle Vocal Suppression (100% Instrumental $\leftrightarrow$ Original Track with 50ms crossfade) |
| `Ctrl + Alt + M` | Toggle Live Microphone Mute |
| `Ctrl + Alt + S` | Start / Stop Live Audio Engine |
| `Ctrl + Alt + 1 / 2 / 3` | Load Saved Streamer Profiles 1, 2, or 3 |

---

## 📋 Issue Submission & Enhancement Policy

Contributions, bug reports, and feature proposals are welcome. To keep triage fast and actionable, please follow these guidelines:

### 1. Reporting Bugs (Issues)
Before opening a new issue, search existing open and closed issues to avoid duplicates. When submitting a bug report, include:
* **Hardware & Driver Environment:** GPU model (e.g., RX 9060 XT), AMD Adrenalin / ROCm version, and Windows 11 build number.
* **Audio Topology:** Selected Music Source (Process Isolation vs. System Master), sample rate (`48 kHz`), and Virtual Audio Cable driver.
* **Diagnostic Logs:** Attach the relevant daily log file from `%AppData%\RocmAudioSplitter\logs\engine_YYYY-MM-DD.log` (accessible via the **Logs** button in the top bar).
* **Reproduction Steps:** Clear step-by-step instructions and observed vs. expected behavior.

### 2. Proposing Enhancements & New Features
Feature requests and architectural proposals should:
* Align with the core **low-latency real-time streaming** scope (avoiding heavy external service dependencies or latency-inducing batch workflows).
* Specify the user workflow benefit (e.g., live broadcast control, GPU efficiency, audio routing flexibility) and any expected UI or DSP impact.
* Check the **Product Roadmap** below to see if the capability is already scheduled for an upcoming release.

### 3. Pull Request Standards
* Target `.NET 10` with zero compiler warnings (`0 Warnings, 0 Errors`).
* Keep real-time audio render/mixer loops (`DualBusStreamEngine`) **allocation-free** (`Span<T>`, pre-allocated buffers, no LINQ or boxing on audio threads).
* Test changes with both Process-Isolated Loopback and OBS / Virtual Cable routing before submitting.

---

## 🗺️ Product Roadmap (Upcoming Releases)

### ✅ v1.0.0 GA (Current Release)
* AMD ROCm 7.1 + DirectML fallback ONNX inference engine (`MDX-Net`).
* Windows 11 per-process WASAPI loopback isolation & dual-bus delay-compensated mixer.
* Avalonia 11 dark-mode console with 60 FPS VU meters, Win32 Global Hotkeys, JSON presets, 440 Hz OBS test tone, and Safe-Mode Bypass watchdog.

### 🚀 v1.1.0 — Stream Deck & Remote Control Integration
* **Local WebSocket & HTTP Control API:** Lightweight localhost server enabling native **Elgato Stream Deck**, **Touch Portal**, and **SAMMI** buttons to toggle vocal suppression, mute mic, switch presets, and read live telemetry.
* **Customizable Hotkey Remapping UI:** In-app shortcut editor allowing users to rebind global hotkeys and save keymaps inside `profiles.json`.
* **Headphone Sidetone Mix:** Optional zero-latency or delay-matched mic sidetone fader on Bus 1.

### 🎛️ v1.2.0 — Live Vocal Chain & Multi-Stem Routing
* **Built-In Vocal DSP Rack:** Real-time noise gate, 3-band parametric EQ, compressor, and subtle room reverb on the live microphone channel before mixing into Bus 2.
* **4-Stem Separation Mode (Vocals, Drums, Bass, Other):** Support for `HTDemucs` / multi-stem ONNX models with individual mute/solo toggles for musicians practicing specific instrument parts.
* **Automatic Sample-Rate & Device Hot-Swap Recovery:** Seamless re-initialization if a USB headset or audio interface disconnects mid-stream.

### 🌐 v1.3.0 — Linux PipeWire Native Support & Next-Gen Models
* **Native Linux Backend (PipeWire + ROCm / MIGraphX):** Full parity on Linux desktops using native PipeWire virtual sinks/sources and AMD HIP/MIGraphX execution providers.
* **Mel-Band RoFormer & FP16 TensorRT/MIGraphX Graph Caching:** Support for ultra-high-SDR transformer separation architectures with persistent compiled GPU graph caching for instant cold starts.

---

## 📄 License

Distributed under the MIT License. See `LICENSE` for more information.
