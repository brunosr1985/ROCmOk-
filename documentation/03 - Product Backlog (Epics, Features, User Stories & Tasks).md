# Product Backlog: ROCm Real-Time Vocal Splitter (.NET 10)

# Executive Summary & Architecture Overview

This backlog outlines the software requirements and engineering tasks for a low-latency, real-time vocal separation desktop application built targeting .NET 10 and AMD ROCm hardware acceleration. The application isolates vocals from arbitrary audio streams (e.g., Spotify, Chrome, system sound) and routes clean instrumental backing tracks mixed with live microphone inputs directly to streaming software (OBS Studio, Meld Studio) and local monitoring gear.

# Epic 1: ROCm AI Inference & Vocal Separation Core (MVP Priority #1)

Core infrastructure for hardware acceleration, ONNX Runtime setup, audio DSP tensor pipelines, and real-time streaming inference using windowed overlap-add techniques.

## Feature 1.1: AMD ROCm Runtime Detection & Model Management [DONE - M1]

| Field | Details |
| --- | --- |
| User Stories | US 1.1.1:  As a streamer using an AMD GPU, I want the application to automatically detect my RDNA3/RDNA4 hardware and ROCm/HIP runtime on startup so that AI inference runs with maximum GPU acceleration. US 1.1.2:  As an end user, I want the app to automatically download, verify (SHA-256), and load optimized ONNX vocal-separation models (e.g., MDX-Net, HTDemucs-vocals) into GPU VRAM so that I am ready for real-time processing without manual configuration. |
| Acceptance Criteria | 1. App successfully enumerates AMD GPUs via HIP/ROCm management APIs.2. Gracefully notifies the user if hardware is unsupported (falling back safely).3. Downloads models missing locally and verifies SHA-256 hashes before loading.4. Allocates model resources in VRAM and completes a dummy inference warm-up pass within 3 seconds of boot. |

### Concrete Engineering Tasks

- Task 1.1.1.1: Implement GpuHardwareProbeService using native C++ wrappers around HIP/ROCm libraries to query device ID, architecture family (RDNA3/RDNA4), driver version, and available VRAM.
- Task 1.1.1.2: Define the high-level IInferenceEngine interface in .NET 10, encapsulating initialization, tensor allocation, asynchronous execution, and resource disposal.
- Task 1.1.1.3: Build OnnxRocmEngine implementing IInferenceEngine, configuring OrtROCMProviderOptions and MIGraphX session flags for Microsoft.ML.OnnxRuntime.Gpu.
- Task 1.1.2.1: Implement ModelManager to handle remote downloading of ONNX models (MDX-Net / HTDemucs) with progress reporting and SHA-256 checksum verification.
- Task 1.1.2.2: Add a warm-up sequence executing zero-valued dummy tensors through the execution provider to pre-compile execution graphs and eliminate real-time startup stutter.

## Feature 1.2: Offline File Vocal Removal (First Deliverable) [DONE - M1]

| Field | Details |
| --- | --- |
| User Stories | US 1.2.1:  As a content creator, I want to load a local WAV, MP3, or FLAC file and separate it into dedicated Instrumental and Vocal stems using ROCm acceleration so that I can create high-quality backing tracks offline. |
| Acceptance Criteria | 1. User can select any valid local audio file (WAV/MP3/FLAC).2. Pipeline decodes and resamples audio cleanly to float32 44.1kHz/48kHz stereo.3. Audio is split into stems via GPU acceleration and exported as 24-bit WAV files.4. Application outputs Real-Time Factor (RTF) metrics upon completion. |

### Concrete Engineering Tasks

- Task 1.2.1.1: Integrate NAudio / MediaFoundation decoders to convert input files into a unified 32-bit floating-point planar or interleaved stereo format.
- Task 1.2.1.2: Implement short-time Fourier Transform (STFT) or time-domain tensor formatting tailored to model requirements (e.g., frequency-domain magnitude/phase frames).
- Task 1.2.1.3: Implement batch processing loop submitting frame chunks to IInferenceEngine while tracking completion percentages.
- Task 1.2.1.4: Construct audio file export writers for stems (WAV 24-bit) and integrate benchmark logging computing processing time versus track duration (RTF ratio).

## Feature 1.3: Streaming Chunked Inference & Overlap-Add (OLA) Engine [DONE - M2 + M3]

| Field | Details |
| --- | --- |
| User Stories | US 1.3.1:  As a live streamer, I want continuous live audio buffers to be processed with configurable window and hop sizes without audible clicking or phase artifacts so that vocal removal sounds natural and transparent. |
| Acceptance Criteria | 1. Processing pipeline runs continuously on live audio with sub-150ms roundtrip processing latency.2. Zero heap allocations occur during standard inference loop execution.3. Overlap-Add (OLA) with Hann windowing reconstructs continuous audio without clicking or boundary artifacts. |

### Concrete Engineering Tasks

- Task 1.3.1.1 [DONE - M1 Foundation]: Build a low-latency, zero-allocation lock-free AudioRingBuffer utilizing .NET 10 Span<T> and Memory<T> for concurrent audio sample reading/writing.
- Task 1.3.1.2: Implement windowing module supporting Hann and Blackman-Harris windowing functions applied during chunk isolation and OLA synthesis.
- Task 1.3.1.3: Configure streaming parameters with default window size of 256ms and hop size of 64ms, ensuring real-time execution budget constraints are met.
- Task 1.3.1.4: Move AI inference processing to a dedicated high-priority thread loop (ThreadPriority.Highest) using native thread affinity settings to avoid GC-induced latency spikes.

# Epic 2: Audio Capture & Per-Application Source Isolation [DONE - M2 + M3]

WASAPI audio subsystem integration enabling low-latency system capture, selective process-specific audio loopback (via ProcessLoopbackCapture, AudioProcessEnumerator, and WasapiAudioCaptureService), and high-quality microphone input conditioning (via MicrophoneCaptureService).

## Feature 2.1: System & Per-Process Audio Capture (Windows WASAPI)

| Field | Details |
| --- | --- |
| User Stories | US 2.1.1:  As a streamer, I want to select either the global system audio or a specific application (e.g., Spotify, Chrome, Edge, VLC) as the music source so that game sound effects and voice chat alerts bypass vocal isolation. |
| Acceptance Criteria | 1. User can choose global output device loopback or target specific running executable processes.2. Windows process-specific capture cleanly isolates selected app audio.3. Automatic high-quality sample-rate conversion handles varying hardware rates down to standard 48kHz stereo float32. |

### Concrete Engineering Tasks

- Task 2.1.1.1: Implement WasapiDeviceEnumerator to list active multimedia playback and capture endpoints using COM interfaces.
- Task 2.1.1.2: Integrate Windows 10/11 ActivateAudioInterfaceAsync using AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS for targeted process audio hooking.
- Task 2.1.1.3: Build high-performance resampler pipeline using SpeexResampler or NAudio WpfAudioResampler to standardize incoming buffers to 48kHz stereo float32.
- Task 2.1.1.4: Implement automatic recovery mechanisms for process crash or output device sample rate state changes.

## Feature 2.2: Microphone Input Capture & Pre-Processing

| Field | Details |
| --- | --- |
| User Stories | US 2.2.1:  As a live performer, I want to select my physical microphone input interface with low-latency capture and apply basic gain control and filtering so my voice is clean before being combined into the final mix. |
| Acceptance Criteria | 1. Physical audio input devices (USB/XLR interfaces) can be selected via configuration.2. Input latency is minimized through low-latency WASAPI Exclusive or Event-Driven Shared Mode.3. Audio chain includes gain adjustments, 80Hz high-pass filter, and an adjustable noise gate. |

### Concrete Engineering Tasks

- Task 2.2.1.1: Build WasapiInputCapture wrapper operating in low-latency event-driven mode.
- Task 2.2.1.2: Implement mono-to-stereo buffer upmixing filter to convert single-channel mic inputs to matching dual-channel stereo streams.
- Task 2.2.1.3: Implement DSP chain processing modules: digital input gain amplifier (+/- 20dB), Butterworth 2nd-order 80Hz high-pass filter for low-end rumble cut, and a threshold-based noise gate.

# Epic 3: Dual-Bus Routing, Mixing & Latency Synchronization [DONE - M2 + M3]

Real-time audio routing engine managing sample-accurate delay compensation using SampleDelayLine, floating-point mixing, and dual output bus targeting for monitoring and stream broadcasting via DualBusStreamEngine.

## Feature 3.1: Sample-Accurate Delay Compensation Engine

| Field | Details |
| --- | --- |
| User Stories | US 3.1.1:  As a live singer, I want my monitor music and microphone input delayed to match the exact latency of the ROCm AI processing model so that my singing stays synchronized with the music track on stream. |
| Acceptance Criteria | 1. Real-time latency calculator dynamically measures total AI model processing + OLA frame delays.2. Circular delay lines accurately delay unprocessed audio streams down to the exact sample frame.3. User-facing manual offset adjustments (+/- 50ms) allow compensation for unique audio interface buffer delays. |

### Concrete Engineering Tasks

- Task 3.1.1.1: Develop dynamic calculation module measuring rolling average processing delay of IInferenceEngine combined with OLA overlap window sizes.
- Task 3.1.1.2: Build high-performance SampleDelayLine circular buffer allowing variable sample-accurate read offsets up to 1000ms.
- Task 3.1.1.3: Implement routing delay compensation node feeding unprocessed Headphone Monitor and Live Microphone buses to sync aligned frames with AI output.
- Task 3.1.1.4: Expose manual offset control parameters to compensate for driver-level kernel streaming variances.

## Feature 3.2: Dual Output Bus Routing (Headphones + Virtual Stream Device)

| Field | Details |
| --- | --- |
| User Stories | US 3.2.1:  As a streamer, I want to send original full music to my physical headphones while routing the AI-separated instrumental track plus my live microphone to a Virtual Audio Cable for OBS Studio. |
| Acceptance Criteria | 1. Independent target configuration for physical monitor output and virtual audio output device.2. Real-time float32 mixer accurately sums instrumental track and microphone input with soft-knee limiting.3. Optional low-latency mic sidetone toggle available for headphone monitoring output. |

### Concrete Engineering Tasks

- Task 3.2.1.1: Implement dual rendering pipeline managing concurrent output instances of WasapiOut.
- Task 3.2.1.2: Add auto-detection logic for virtual audio drivers (VB-Audio Cable, Virtual Audio Cable, Virtual Audio Device).
- Task 3.2.1.3: Develop FloatAudioMixer with embedded soft-knee brickwall peak limiter preventing digital clipping when summing multiple channels.
- Task 3.2.1.4: Implement optional Sidetone audio routing node with independent volume control routing live mic signal to the physical headphone bus.

# Epic 4: .NET 10 Desktop Application & Live Telemetry UI [DONE - M4]

User interface developed in Avalonia UI (RocmAudioSplitter.Desktop / MainWindowViewModel) delivering cross-platform-ready rendering, 60fps 5-channel peak VU meters, low-overhead controls, and real-time ROCm latency performance telemetry HUD.

## Feature 4.1: Main Routing & Mixer Console (Avalonia UI)

| Field | Details |
| --- | --- |
| User Stories | US 4.1.1:  As a user, I want a dark-mode mixer dashboard where I can configure audio source selection, toggle vocal removal on/off, adjust stream volume levels, and observe 60fps VU meters. |
| Acceptance Criteria | 1. Clean visual layout featuring dark theme UI optimized for secondary stream monitors.2. Real-time 60fps peak/RMS level meters rendering smoothly across all inputs and outputs.3. Responsive fader controls with mute buttons and a Vocal Suppression Strength / Dry-Wet blend slider. |

### Concrete Engineering Tasks

- Task 4.1.1.1: Scaffold .NET 10 desktop project structure using Avalonia UI (MVVM design pattern with ReactiveUI or CommunityToolkit.Mvvm).
- Task 4.1.1.2: Implement custom SKGLControl / WriteableBitmap 60fps VU meter controls rendering dynamic RMS and peak-hold indicators for 5 audio channels: Input Music, AI Instrumental, Live Mic, Headphone Out, Stream Out.
- Task 4.1.1.3: Build modern dark-theme dashboard UI layout containing faders, audio source drop-down pickers, routing matrices, and mute controls.
- Task 4.1.1.4: Bind Vocal Suppression Strength control to a real-time crossfading DSP node blending raw input audio with isolated stem audio.

## Feature 4.2: ROCm Performance & Latency HUD

| Field | Details |
| --- | --- |
| User Stories | US 4.2.1:  As an advanced user, I want to monitor GPU hardware performance, VRAM consumption, inference latency, and audio buffer state in real time so I can prevent stream dropouts. |
| Acceptance Criteria | 1. Dashboard displays live updates of GPU clock/load, VRAM usage, and inference duration.2. Visual indicators display percentile latency metrics (p50, p99).3. Prominent warning status badges trigger if processing times exceed the real-time frame hop budget. |

### Concrete Engineering Tasks

- Task 4.2.1.1: Build TelemetryAggregatorService collecting system metrics on a 250ms interval.
- Task 4.2.1.2: Implement rolling statistical accumulator calculating p50, p95, and p99 millisecond latency execution frames.
- Task 4.2.1.3: Design performance telemetry HUD view displaying VRAM usage, inference compute times, and streaming buffer state.
- Task 4.2.1.4: Implement audio buffer health monitor triggering visual alert warnings upon buffer underflows or overruns (xruns).

# Epic 5: Streamer Workflow Integration & Hotkeys

Workflow enhancements enabling seamless live performance control via GlobalHotkeyService (Win32 RegisterHotKey message-only window) and ProfileManager (JSON preset persistence in %AppData%\RocmAudioSplitter\profiles.json) with smooth 50ms crossfade state transitions.

## Feature 5.1: Global Hotkeys & Profile Presets [DONE - Epic 5]

| Field | Details |
| --- | --- |
| User Stories | US 5.1.1:  As a live streamer, I want to toggle vocal separation on/off, control microphone state, or switch performance profiles via global keyboard shortcuts without taking focus off my game. |
| Acceptance Criteria | 1. Configurable global hotkeys function correctly while third-party apps and games have system focus.2. Switching profiles or toggling vocal separation causes no popping, clicking, or digital audio dropouts.3. Routing profiles and custom parameter mappings persist reliably in JSON format. |

### Concrete Engineering Tasks

- Task 5.1.1.1: Implement low-level Windows API keyboard hook (SetWindowsHookEx) service for system-wide hotkey interception.
- Task 5.1.1.2: Create JSON-backed ProfileManager allowing users to save and load named routing configurations (e.g., "Karaoke / Live Singing", "Normal Gaming / Talk").
- Task 5.1.1.3: Implement 50ms equal-power linear crossfade DSP node for state transitions (e.g., toggling vocal processing or switching presets) to prevent transient audio pops.
- Task 5.1.1.4: Create local HTTP / WebSocket command listener endpoint to allow integration with hardware controllers like Elgato Stream Deck.

# Epic 6: Resilience, Fallback & Packaging [DONE - M5 / v1.0.0 GA]

Fault tolerance layers handling system load surges, automatic compute provider degradation, self-contained deployment, and initial setup diagnostics (utilizing AppLogger, a real-time hop budget watchdog inside DualBusStreamEngine with automatic Safe-Mode Bypass triggered after 5 consecutive GPU overruns, along with UI alert banner and manual watchdog reset controls).

## Feature 6.1: Hardware Fallback & Safe-Mode Recovery [DONE - M5]

| Field | Details |
| --- | --- |
| User Stories | US 6.1.1:  As a user, I want the application to automatically bypass processing or drop down to alternative compute modes if my GPU is overloaded by a game, ensuring system audio never crashes. |
| Acceptance Criteria | 1. Application detects consecutive inference frame budget overruns and takes corrective action.2. System gracefully falls back to DirectML or bypass mode without crashing live audio playback.3. Detailed diagnostic logs capture underlying platform errors for troubleshooting. |

### Concrete Engineering Tasks

- Task 6.1.1.1: Build InferenceWatchdog monitoring the processing queue for consecutive frame budget timeouts.
- Task 6.1.1.2: Implement dynamic Execution Provider fallback logic shifting runtime from ROCm to DirectML or enabling full audio bypass if primary inference fails.
- Task 6.1.1.3: Create comprehensive diagnostic log recorder capturing audio driver events, hardware initialization steps, and exception stack traces to local storage.

## Feature 6.2: Windows Installer & First-Run Setup Wizard [DONE - M5]

| Field | Details |
| --- | --- |
| User Stories | US 6.2.1:  As a new user installing the app, I want a guided onboarding setup wizard that verifies my GPU features, checks audio drivers, and plays test signals so I can confirm setup rapidly. |
| Acceptance Criteria | 1. Installer packages all dependencies into a single, self-contained setup program.2. Guided onboarding wizard checks AMD hardware capabilities and virtual audio drivers on first launch.3. Interactive test tone generation allows verifying routing configurations inside OBS Studio. |

### Concrete Engineering Tasks

- Task 6.2.1.1: Configure Inno Setup / WiX Toolset script to package a single self-contained .NET 10 desktop application release.
- Task 6.2.1.2: Implement 3-step setup onboarding wizard in Avalonia UI validating:
- AMD Hardware & ROCm/HIP driver readiness.
- Virtual Audio Cable presence and WASAPI endpoints.
- OBS Studio / Stream software audio signal detection.
- Task 6.2.1.3: Integrate DiagnosticToneGenerator producing a 440Hz pink noise / sine calibration tone, perform startup hardware/VAC readiness checks, automate deployment via publish-release.ps1, and compile release packages using Inno Setup (installer.iss).

# Priority & Dependency Matrix

[Epic 1: Inference Engine] ──────► [Epic 2: Audio Capture] ──────► [Epic 3: Mixing & Routing]

                                                                          │

                                                                          ▼

[Epic 6: Installer & Safety] ◄──── [Epic 5: Hotkeys & Presets] ◄──── [Epic 4: Avalonia UI Dashboard]

## Milestone Release Plan

- Milestone 1 (Alpha) [COMPLETED]: Core ROCm Engine + Offline File Vocal Removal (Features 1.1, 1.2).
- Milestone 2+3 (Live Dual-Bus Engine) [COMPLETED]: Streaming WASAPI Input/Capture + OLA Inference Engine + Dual Bus Routing (Features 1.3, 2.1, 2.2, 3.1, 3.2).
- Milestone 4 (Desktop UI & Integration) [COMPLETED]: Avalonia UI Dashboard + Global Hotkeys + Telemetry HUD (Features 4.1, 4.2, 5.1).
- Milestone 5 (GA Release) [COMPLETED]: Hardware Fallback + Safe-Mode Recovery + Production Installer & Setup Diagnostics (Features 6.1, 6.2) — v1.0.0 GA Released.
