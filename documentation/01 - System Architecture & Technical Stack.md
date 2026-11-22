# System Architecture & Technical Stack

# Real-Time ROCm Vocal-Splitting Desktop Application in .NET 10

# 1. Executive Vision & Core Use Case

The target application delivers high-performance, real-time vocal separation for live streaming, content creation, and live performance workflows. Operating on modern Windows and Linux environments, the system intercepts application-specific or system-wide digital audio, separates vocal and instrumental stems using hardware-accelerated deep neural networks on AMD GPUs via AMD ROCm, and routes synchronized audio streams to monitoring endpoints and broadcast software (such as OBS Studio and Meld Studio).

The engine manages two dedicated, ultra-low-latency audio routing buses operating concurrently:

## Monitoring Bus (Local Headphones)

Routes the full original audio track (Music + Original Vocals) directly to the performer's local playback device. To ensure the singer hears the accompaniment naturally without phase cancellation, echo, or perceptual latency drift against their own voice, the monitoring engine injects precise, dynamically measured sample-accurate delay lines.

## Stream / Broadcast Bus (Virtual Output to OBS / Meld Studio)

Constructs the final live mix by taking the real-time AI-isolated instrumental stem (Original Audio minus Main Vocals), blending it with the performer’s live microphone input, and routing the combined signal to a dedicated virtual audio interface (e.g., VB-Audio Cable). The live microphone signal is routed through an identical delay pipeline matched to the neural network's exact chunk-processing latency, maintaining absolute temporal alignment between the live voice and the accompaniment track on stream.

                                +---------------------------+

                                |  Target Audio Source      |

                                |  (Process / System Audio) |

                                +-------------+-------------+

                                              |

                                              v

                                +-------------+-------------+

                                |   Audio Interceptor &     |

                                |   Demux (WASAPI / Pipe)   |

                                +------+--------------+------+

                                       |              |

                +----------------------+              +-----------------------+

                |                                                             |

                v                                                             v

  +-------------+-------------+                                 +-------------+-------------+

  |   Branch A: Monitoring    |                                 |     Branch B: Separation    |

  | (Local Latency Buffer)    |                                 | (GPU Neural Pipeline)       |

  +-------------+-------------+                                 +-------------+-------------+

                |                                                             |

                | Sample Delay = T(Inference)                                 | STFT -> AMD ROCm ONNX

                v                                                             v Reconstruct Instrumental

  +-------------+-------------+                                 +-------------+-------------+

  |  Hardware Output Driver   |                                 |   Stream Audio Synchronizer |

  |   (Direct Headphones)     |                                 | (Mixes Delayed Mic + Audio) |

  +---------------------------+                                 +-------------+-------------+

                                                                              |

                                                                              v

                                                                +-------------+-------------+

                                                                |   Virtual Audio Line Out  |

                                                                |   (To OBS / Meld Studio)  |

                                                                +---------------------------+



# 2. Recommended .NET 10 Desktop Stack

To achieve zero-GC-pause execution on real-time audio threads alongside high-throughput GPU memory transfers, the application utilizes .NET 10 and native desktop primitives.

## System Layer Architecture

| Layer | Recommended Technology | Architectural Rationale & Implementation Details |
| --- | --- | --- |
| Core Runtime | .NET 10 (C# 14) with Native AOT | Full Native AOT compilation eliminates CLR JIT overhead and reduces cold-start memory footprints. Strict zero-allocation audio loops utilize  Span<T> ,  ReadOnlySpan<T> , and pinned memory pointers in  AudioRingBuffer  to guarantee the GC never triggers on audio render threads. |
| Desktop UI | Avalonia UI 11+ (Skia Engine) | Cross-platform UI toolkit using hardware-accelerated Skia rendering. Offers smooth 60fps telemetry visualizers, real-time peak/RMS VU meters, and dynamic latency graphs across Windows 11 and Linux environments. |
| Audio Capture & DSP | NAudio + WASAPI + MathNet.Numerics | Uses WASAPI Loopback (Shared/Exclusive mode) and Win32  PROCESS_LOOPBACK_MODE .  MdxStftProcessor  leverages MathNet.Numerics for forward/inverse STFT, periodic Hann windowing, and Hermitian symmetry reconstruction. |
| Virtual Audio Routing | Virtual Audio Cable (VAC) / VB-Audio Integration | Direct integration with kernel-level virtual audio endpoints, exposing a clean virtual capture endpoint to broadcast suites (OBS Studio, Meld Studio). |
| AI Acceleration | ONNX Runtime Managed/DirectML 1.21.0 | Generic  SessionOptions.AppendExecutionProvider("ROCM", ...)  with fallback to MIGraphX and DirectML ( AppendExecutionProvider_DML(0) ), featuring a 2 GB VRAM limit ( gpu_mem_limit ). |

## AI Inference & ROCm Engine Configuration

The engine supports native execution paths tailored to target operating systems and hardware generations:

                         +-----------------------------------+

                         |  Model Manager & Engine Host      |

                         +-----------------+-----------------+

                                           |

                    +----------------------+----------------------+

                    |                                             |

                    v                                             v

     +--------------+--------------+               +--------------+--------------+

     | Windows 11 Native Engine    |               | Linux Native Engine         |

     | (ROCm 7.2+ / DirectML)      |               | (HIP Runtime / MIGraphX)     |

     +--------------+--------------+               +--------------+--------------+

                    |                                             |

                    +----------------------+----------------------+

                                           |

                                           v

                         +-----------------------------------+

                         |  ONNX Model Inference Execution   |

                         |  (MDX-Net / Mel-Band RoFormer)    |

                         +-----------------------------------+

### Windows 11 Environment

AMD ROCm 7.1 with amdhip64_7.dll delivers hardware-accelerated execution targeting AMD Radeon RX 9060 XT (RDNA 4 / gfx12xx) GPUs with 16 GB VRAM (validated via 64-bit Windows registry HardwareInformation.qwMemorySize to bypass the 4095 MB 32-bit WMI cap). The C# stack uses Microsoft.ML.OnnxRuntime.Managed 1.21.0 and Microsoft.ML.OnnxRuntime.DirectML 1.21.0 with generic provider invocation SessionOptions.AppendExecutionProvider("ROCM", ...) and fallback to MIGraphX or DirectML (AppendExecutionProvider_DML(0)). A VRAM memory arena cap of 2 GB (gpu_mem_limit) ensures inference safely coexists with games and OBS/Meld Studio encoding pipelines.

### Linux Environment

Native ROCm stack deployment utilizes AMD's HIP runtime combined with the MIGraphX execution provider. Shared-memory inter-process communication (IPC) sidecars handle custom PyTorch model structures when direct ONNX exports are unavailable.

### Model Architecture

The separation engine defaults to frequency-domain MDX-Net models (UVR_MDXNET_KARA_2.onnx for lead-vocal removal while preserving backing harmonies, and UVR-MDX-NET-Inst_HQ_5.onnx for full instrumental isolation) operating with 4D tensor shapes [1, 4, dim_f, dim_t]. Signal transformation is handled by MdxStftProcessor (MathNet.Numerics forward/inverse STFT, periodic Hann windowing, and Hermitian symmetry iFFT reconstruction) alongside a pinned lock-free single-producer single-consumer AudioRingBuffer.

# 3. Real-Time Audio DSP & Latency Compensation Pipeline

Real-time neural separation introduces non-negotiable structural latencies comprised of window framing constraints, FFT time-frequency conversion, and GPU execution delays. The pipeline mitigates phase artifacts and timing drift through a custom synchronization architecture.

[WASAPI Capture (10ms Frames)]

             |

             v

   +---------+---------+

   | Audio Demux Buffer|

   +----+---------+----+

        |         |

        |         +-------------------------------------------------------------+

        | (Original Stereo Stream)                                              | (Live Microphone Input)

        v                                                                       v

+-------+-----------------------+                               +---------------+---------------+

| Branch A: Monitoring Delay    |                               | Branch B: Microphone Delay    |

| Line                          |                               | Line                          |

| Delay = T(Alg) + T(GPU)       |                               | Delay = T(Alg) + T(GPU)       |

+-------+-----------------------+                               +---------------+---------------+

        |                                                                       |

        v                                                                       |

+-------+-----------------------+                                               |

| Hardware Monitoring Output    |                                               |

| (Headphones)                  |                                               |

+-------------------------------+                                               |

                                                                                |

                                                                                |

[WASAPI Capture Stream]                                                         |

        |                                                                       |

        v                                                                       |

+-------+-----------------------+                                               |

| Overlap-Add (OLA) STFT        |                                               |

| Frame Aggregator              |                                               |

| Context: 256ms | Hop: 64ms    |                                               |

+-------+-----------------------+                                               |

        |                                                                       |

        v                                                                       |

+-------+-----------------------+                                               |

| AMD ROCm Tensor Execution     |                                               |

| (FP16 ONNX Inference)         |                                               |

+-------+-----------------------+                                               |

        |                                                                       |

        v                                                                       |

+-------+-----------------------+                                               |

| Inverse STFT & Hann Window    |                                               |

| Boundary Resynthesis          |                                               |

+-------+-----------------------+                                               |

        |                                                                       |

        v                                                                       |

+-------+-----------------------+                                               |

| Stream Bus Mixer              |<----------------------------------------------+

| Instrumental Stem + Voice     |

+-------+-----------------------+

        |

        v

+-------+-----------------------+

| Virtual Audio Output Device   |

| (To OBS Studio / Meld Studio) |

+-------------------------------+

## Overlap-Add (OLA) Windowing Pipeline

Hop 0  : |====== Window 256ms ======|  --> GPU Inference -> Output Frame 0

Hop 1  :        |====== Window 256ms ======|  --> GPU Inference -> Output Frame 1

Hop 2  :               |====== Window 256ms ======|  --> GPU Inference -> Output Frame 2

Time   : -------> 64ms | 64ms | 64ms | 64ms -------->



Audio frames arrive from WASAPI capture buffers in small intervals (e.g., $10\text{ ms}$ or $20\text{ ms}$ at $48\text{ kHz}$).

- Aggregation: Samples feed into a circular ring buffer that builds window frames of size $N = 2048$ to $4096$ samples ($42.6\text{ ms}$ to $85.3\text{ ms}$ at $48\text{ kHz}$).
- Hop Overlap: Processing runs on a continuous sliding scale with a $75%$ overlap ($25%$ hop size, $D = N/4$).
- Windowing Function: Each chunk is multiplied by a periodic Hann window:

$$w[n] = 0.5 \left(1 - \cos\left(\frac{2\pi n}{N}\right)\right), \quad 0 \le n < N$$

- Resynthesis: Reconstructed spectrum chunks pass through Inverse STFT processing. The output frames are accumulated using an overlap-add algorithm to reconstruct continuous audio without transient clicks:

$$y[n] = \sum_{k=-\infty}^{\infty} y_k[n - kD] \cdot w[n - kD]$$

## Delay Line Synchronization Mechanics

System latency consists of two elements:

$$T_{\text{Total Latency}} = T_{\text{Algorithmic}} + T_{\text{Inference}}$$

- Algorithmic Latency ($T_{\text{Algorithmic}}$): Time required to collect enough future audio samples to fill the input context window (typically equivalent to one full window length minus one hop size).
- Inference Latency ($T_{\text{Inference}}$): Execution time for the GPU to process the ONNX model tensor pass (typically $8\text{ ms}$–$18\text{ ms}$ on RDNA3 architecture).

Timeline (Milliseconds):

0ms            T_Alg          T_Alg + T_Infer

|--------------|--------------|---------------------------------------------->

[ Capture Window ]

               [ GPU Execution ]

                              [ Isolated Instrumental Output Available ]

0ms                                    T_Total

|--------------------------------------|-------------------------------------->

[ Mic / Monitor Signal Delayed in Ring Buffer ]

                                       [ Delayed Audio Plays in Sync ]



To maintain alignment across all paths, the engine tracks execution timings over a rolling 100-frame average. The target delay $D_{\text{target}}$ applied to both the headphone monitor branch and the broadcast microphone stream is continually updated:

$$D_{\text{target}} = \text{FrameSize} + \bar{T}{\text{Inference}} + T{\text{DriverBuffer}}$$

The input sample delay is implemented using a lock-free ring buffer:

public sealed class RingBufferDelayLine

{

    private readonly float[] _buffer;

    private int _writeIndex;

    private int _delaySamples;

    public RingBufferDelayLine(int maxDelaySamples)

    {

        _buffer = GC.AllocateArray<float>(maxDelaySamples, pinned: true);

    }

    public void SetDelay(int delaySamples)

    {

        Volatile.Write(ref _delaySamples, delaySamples);

    }

    public void ProcessBlock(ReadOnlySpan<float> input, Span<float> output)

    {

        int delay = Volatile.Read(ref _delaySamples);

        int capacity = _buffer.Length;

        for (int i = 0; i < input.Length; i++)

        {

            _buffer[_writeIndex] = input[i];

            int readIndex = (_writeIndex - delay + capacity) % capacity;

            output[i] = _buffer[readIndex];

            _writeIndex = (_writeIndex + 1) % capacity;

        }

    }

}



# 4. Key Technical Risks & Mitigations

| Risk Domain | Identified Threat | System Impact | Technical Mitigation Strategy |
| --- | --- | --- | --- |
| GPU VRAM Contention | Concurrent high-VRAM applications (3D gaming engines, OBS NVENC/AMF encoding pipelines, dynamic canvas compositions). | Out-of-memory (OOM) faults, kernel panics, frame dropping, GPU context resetting. | Implement fixed-size pre-allocated VRAM pools via ONNX Runtime Memory Arena limits ( OrtROCMProviderOptions ). Cap VRAM usage to $\le 1.8\text{ GB}$. Enable DirectML dynamic system RAM allocation fallbacks if hardware VRAM usage crosses $90%$. |
| Buffer Glitches & Underruns | High CPU utilization spiking render thread execution times beyond WASAPI buffer windows. | Audio pops, clicking, dropouts, temporal pitch shifting. | Lock execution threads to dedicated CPU cores via thread affinity APIs ( Thread.CurrentThread.Priority = ThreadPriority.Highest ). Utilize lock-free ring buffers ( System.Threading.Channels  or custom pinned memory pointers) to prevent thread blocking. |
| ROCm Runtime Compatibility | Divergent user ROCm driver versions, missing HIP binaries, or driver mismatch on Windows 11 systems. | Application launch failures, DLL initialization exceptions, execution crashes. | Implement an automated hardware boot-check sequence. At startup, validate ROCm environment links; if drivers are missing or incompatible, dynamically route processing to DirectML without terminating the user process. |
| Phase & Chunk Discontinuity | Boundary phase misalignment between consecutive overlapping STFT inference chunks. | Audible robotic artifacts, phase flanging, periodic metallic buzzes in instrumental stem output. | Use a $75%$ STFT overlap ratio ($25%$ hop size) paired with a square-root Hann window applied across both forward transformation and reverse synthesis steps, satisfying the Constant Overlap-Add (COLA) constraint. |

# 5. Implementation Baseline & Validated Architecture (v1.0)

The following implementation baseline reflects the validated hardware setup, runtime decisions, and DSP pipeline achieved in Milestone 1:

- Target Hardware Setup: Validated on AMD Radeon RX 9060 XT (RDNA 4 / gfx12xx architecture, 16 GB VRAM read via 64-bit Windows registry HardwareInformation.qwMemorySize to avoid 32-bit WMI 4095 MB truncation) running AMD ROCm 7.1 (amdhip64_7.dll).
- ONNX Runtime C# Execution Stack: Integrated Microsoft.ML.OnnxRuntime.Managed 1.21.0 and Microsoft.ML.OnnxRuntime.DirectML 1.21.0 using generic invocation SessionOptions.AppendExecutionProvider("ROCM", ...) with dynamic fallback to MIGraphX and DirectML (AppendExecutionProvider_DML(0)). Enforces a 2 GB VRAM memory limit (gpu_mem_limit) to guarantee seamless coexistence with background games and streaming suites (OBS / Meld Studio).
- Selected AI Models & DSP Engine: Deployed frequency-domain MDX-Net ONNX models—UVR_MDXNET_KARA_2.onnx (lead vocal removal preserving backing harmonies) and UVR-MDX-NET-Inst_HQ_5.onnx (full instrumental extraction)—using tensor input shapes [1, 4, dim_f, dim_t]. Supported by MdxStftProcessor (MathNet.Numerics forward/inverse STFT, periodic Hann windowing, Hermitian symmetry reconstruction) and a pinned lock-free single-producer single-consumer AudioRingBuffer.
- Real-Time Streaming & Process Isolation (M2 + M3): Integrated ProcessLoopbackCapture using Win32 ActivateAudioInterfaceAsync (VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK + PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE), and deployed DualBusStreamEngine operating on a 10ms fixed Stopwatch mixer clock with idle digital silence injection and automatic headphone mute fallback on system-wide loopback to prevent acoustic/digital feedback loops.
- Avalonia Desktop UI & Streamer Controls (M4 + Epic 5): Built RocmAudioSplitter.Desktop (Avalonia 11.2.5 + CommunityToolkit.Mvvm) featuring 60fps 5-channel peak VU meters, live DSP sliders, GlobalHotkeyService (Win32 RegisterHotKey message-only window for out-of-focus toggling of Vocal Suppression, Mic Mute, and Live Engine), and ProfileManager (JSON presets saved under %AppData%\RocmAudioSplitter\profiles.json with smooth parameter interpolation).
- Production Hardening, Watchdog & Packaging (M5 — v1.0.0 GA Completed): Implemented AppLogger (daily rotating diagnostic logs under %AppData%\RocmAudioSplitter\logs), real-time inference budget watchdog in DualBusStreamEngine (automatic Safe-Mode Bypass fallback after 5 consecutive GPU hop budget overruns with UI alert and manual reset), DiagnosticToneGenerator (440 Hz calibration tone on Bus 2 for OBS/Meld Studio level verification), and self-contained win-x64 release packaging (publish-release.ps1 + Inno Setup installer.iss).
