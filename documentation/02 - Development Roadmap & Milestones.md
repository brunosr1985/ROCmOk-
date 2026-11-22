# Development Roadmap & Milestones

This document outlines the technical delivery strategy, phased milestone schedule, and foundational Architecture Decision Records (ADRs) for building a real-time vocal-splitting desktop application powered by AMD ROCm acceleration and target platform .NET 10.

# 1. Delivery Strategy (Iterative Vertical Slices)

To mitigate technical risk across hardware acceleration, low-latency Digital Signal Processing (DSP), and complex audio routing, the engineering lifecycle relies on an iterative vertical slice strategy. Each milestone builds directly on validated hardware and pipeline guarantees from the previous stage.

M1: Offline ROCm Engine  ──►  M2: Streaming DSP  ──►  M3: Dual-Bus Mixer  ──►  M4: .NET 10 UI  ──►  M5: Hardening

(Inference & Stems)           (Real-Time OLA)        (Delay & Routing)        (Telemetry & UX)       (Production Release)



- Milestone 1 — Offline Proof of Concept: Validate the fundamental ML pipeline first. Establish functional ONNX Runtime bindings targeting AMD ROCm execution providers. Process offline file buffers, extract clean Instrumental and Acapella stems, and prove that AMD GPU acceleration significantly outpaces real-time factor requirements (RTF < 0.3x) before attempting live streaming.
- Milestone 2 — Streaming Audio Engine: Transition from block-based file processing to a continuous live streaming architecture. Implement Windows Audio Session API (WASAPI) loopback capture, lock-free ring buffers, frame chunking, continuous sliding-window inference, and Overlap-Add (OLA) reconstruction to synthesize click-free stream output.
- Milestone 3 — Dual-Bus Routing & Sync: Address complex live-stream audio topologies. Construct a sample-accurate DSP mixer capable of splitting audio into two distinct output buses: an isolated broadcast stream sent to a Virtual Audio Cable (for OBS Studio or Meld Studio), and a low-latency monitoring stream routed to local headphones. Implement dynamic buffer delay lines to keep live microphone inputs perfectly phase-aligned with processed instrumentals.
- Milestone 4 — Native Desktop UI & Control Surface: Wrap the validated engine in a high-performance .NET 10 user interface using Avalonia. Integrate real-time VU meter visualizations, active GPU/VRAM telemetry, per-application audio loopback selectors, stream-ready audio FX (mic gain/EQ), and global hotkey triggers.
- Milestone 5 — Hardening, Optimization & Packaging: Finalize the application for consumer environments. Add automatic device fallback (DirectML/CPU), VRAM budget limiters to prevent frame drops in background games, an automated installer with virtual cable configuration wizards, and perform long-duration stress testing under peak load.

# 2. Phase-by-Phase Roadmap Table

| Phase | Milestone Name | Target Duration | Core Objectives | Primary Epics Covered | Exit Criteria (Definition of Done) |
| --- | --- | --- | --- | --- | --- |
| Phase 1 | M1 — ROCm Inference PoC & Offline Vocal Removal [COMPLETED] | Weeks 1–2 | • Validate ONNX Runtime ROCm (`AppendExecutionProvider("ROCM")`) bindings in C# .NET 10 • Deliver RocmAudioSplitter.Core & RocmAudioSplitter.Cli • Probe AMD GPU hardware (RX 9060 XT RDNA 4, 16 GB VRAM via 64-bit registry, ROCm 7.1 amdhip64_7.dll) via GpuHardwareProbeService • Execute STFT processing & ONNX inference using OnnxRocmSeparationEngine, MdxStftProcessor (UVR_MDXNET_KARA_2.onnx & UVR-MDX-NET-Inst_HQ_5.onnx), and OfflineStemSeparatorService | • Model Packaging & ONNX Export • ROCm / HIP Interop & DirectML Fallback • Offline Audio I/O Pipeline & STFT Processor • Stem Export (Instrumental / Acapella) • Benchmarking Harness | • Successful generation of separated WAV stems from offline files • Real-time factor (RTF) measured below 0.30x on target RX 9060 XT hardware • Zero VRAM leak across consecutive file processing cycles with DirectML fallback verified |
| Phase 2 & 3 Combined | M2 + M3 — Real-Time Streaming DSP, Process Isolation & Dual-Bus Routing [COMPLETED] | Weeks 3–8 | • Capture live audio via WASAPI Loopback (system/per-process via `ActivateAudioInterfaceAsync` / `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`) & mic input • Build lock-free ring buffers, `ProcessLoopbackCapture`, `AudioProcessEnumerator`, and continuous sliding-window OLA streaming inference • Route instrumental stem + microphone to Virtual Audio Cable (for OBS / Meld Studio) via `DualBusStreamEngine` (10ms fixed-clock mixer with idle digital silence injection and loopback feedback protection) • Route full mix / monitor to local headphones with `SampleDelayLine` sample-accurate delay compensation to align mic feed with processed instrumental | • WASAPI Capture & Microphone Engine • Streaming OLA Inference Worker Loop • Dual-Bus Routing Engine • Sample-Accurate Delay Compensation Lines | • Uninterrupted live streaming vocal isolation with sub-120ms latency • Phase-aligned mic and instrumental audio without clicks or buffer underflows • Clean stream ingestion into OBS / Meld Studio via Virtual Audio Cable |
| Phase 4 | M4 — .NET 10 Desktop UI, Telemetry, Global Hotkeys & Streamer Profiles [COMPLETED] | Weeks 9–11 | • Build Avalonia UI 11.2.5 + `CommunityToolkit.Mvvm` desktop interface (`RocmAudioSplitter.Desktop`) in .NET 10 • Implement 60fps 5-channel peak VU meters, live DSP sliders & ROCm latency HUD • Create per-process audio capture selection dropdown • Implement Win32 `RegisterHotKey` (`GlobalHotkeyService`) & JSON presets via `ProfileManager` | • Avalonia UI Dashboard & Themes • Low-Overhead Telemetry Streamer • Windows Audio Session Endpoint Picker • Global Hotkey & Profile System | • UI renders smoothly at 60 FPS while consuming under 3% CPU during live processing • Per-process loopback reliably isolates target applications (e.g., Spotify, Chrome) • User settings, hotkeys, and audio routing presets persist across application restarts |
| Phase 5 [COMPLETED] | M5 — Performance Tuning, Packaging & Resilience [COMPLETED] | Weeks 12–14 | • Implement  AppLogger  infrastructure and dynamic VRAM / real-time inference budget watchdog with Safe-Mode Bypass in  DualBusStreamEngine • Integrate  DiagnosticToneGenerator  (440 Hz calibration tone) for OBS / Meld Studio stream setup • Build self-contained  win-x64  distribution packages via  publish-release.ps1  and Inno Setup installer ( installer.iss ) • Conduct long-duration soak testing and operational performance verification under peak load | • Fallback Execution Provider Engine • Dynamic VRAM / Thread Limiter • WiX / MSIX Installer with Driver Bundling • Long-Duration Soak Testing Framework | • Automatic graceful fallback to DirectML when ROCm drivers are absent • Application survives 4-hour continuous stream stress test under full GPU game load without crashes or audio degradation • Clean one-click installation on pristine Windows 11 environments |

# 3. Architecture Decision Records (ADRs) to Lock Early

The following architectural decisions must be formally evaluated and resolved during Phase 1 to prevent downstream refactoring of core pipeline components.

## ADR-001 (LOCKED): Inference Runtime Selection

- Context: AMD ROCm execution support on Windows can be targeted via direct C# P/Invoke bindings to ONNX Runtime (using the ROCm or MIGraphX Execution Provider) or through a custom native C++ wrapper invoking HIP APIs directly.
- Evaluation Criteria: Pipeline throughput, execution provider initialization stability under .NET 10, cross-platform maintainability, and deployment friction for consumer graphics cards.
- Decision (LOCKED): Standardize on ONNX Runtime targeting ROCm via AppendExecutionProvider("ROCM") with automatic DirectML fallback and a strict 2 GB VRAM allocation limit.
## ADR-002 (LOCKED): Model Selection & Window Size Trade-off

- Context: Advanced vocal separation architectures (MDX-Net, Mel-Band RoFormer, HTDemucs) present distinct trade-offs between isolation quality (SDR rating), inference latency, and VRAM memory footprint.
- Evaluation Criteria: Trade-off between window chunk sizes (e.g., 512ms vs. 256ms vs. 128ms), computational overhead, and output stem fidelity. Real-time streaming requires keeping total algorithmic delay within acceptable bounds for live monitoring.
- Decision (LOCKED): Lock to UVR MDX-Net frequency-domain ONNX models (UVR_MDXNET_KARA_2.onnx and UVR-MDX-NET-Inst_HQ_5.onnx) using [1, 4, dim_f, dim_t] complex STFT tensors to optimize quality vs. real-time latency.
## ADR-003 (LOCKED): Per-Process Audio Capture vs. System-Wide Loopback

- Context: Streamers often want to isolate vocals from specific application channels (e.g., desktop media players or browser streams) rather than splitting the entire system output master bus.
- Evaluation Criteria: Standard WASAPI System Loopback vs. Windows AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS API available in modern Windows 10/11 builds.
- Decision (LOCKED): Standardize on dual-mode WASAPI capture defaulting to Windows 11 Process-Scoped Loopback via ActivateAudioInterfaceAsync with PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE, plus automatic headphone loopback mute fallback when System Master is selected.
## ADR-004 (LOCKED): Virtual Audio Device Strategy

- Context: Routing processed instrumentals to third-party broadcasting software (OBS Studio, Streamlabs, Meld Studio) requires a virtual audio pipeline endpoint.
- Evaluation Criteria: Bundling existing third-party virtual cables (such as VB-Audio Cable) with installer automation versus developing a bespoke Kernel-Mode / WDM Virtual Audio Class Driver.
- Decision (LOCKED): Standardize on VB-Audio Virtual Cable / Virtual Audio Device auto-detection via WasapiDeviceEnumerator for OBS Studio and Meld Studio routing.

# 4. v1.0.0 GA Release Status & Operational Verification

With Milestones 1 through 5 fully completed and core ADRs locked, the platform has achieved v1.0.0 General Availability (GA) status. Final operational verification confirms all 5 milestones have been successfully delivered, including automated soak-test verification and standalone win-x64 release packaging for production deployment.

- Dynamic Watchdogs: Dynamic VRAM and thread limiters to handle concurrent gaming loads without stuttering or latency spikes.
- Installer Automation: Self-contained WiX / Inno Setup packaging with integrated virtual audio cable setup wizard.
- Soak Testing Framework: Automated stress testing across long-duration live streaming sessions.
