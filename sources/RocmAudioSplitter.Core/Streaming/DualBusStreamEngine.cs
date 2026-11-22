using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RocmAudioSplitter.Core.Audio;
using RocmAudioSplitter.Core.Dsp;
using RocmAudioSplitter.Core.Inference;

namespace RocmAudioSplitter.Core.Streaming;

public sealed record ChannelLevelMeters(
    float MusicInputPeak,
    float AiInstrumentalPeak,
    float MicInputPeak,
    float HeadphoneOutPeak,
    float StreamOutPeak);

public sealed record LiveTelemetrySnapshot(
    double LastInferenceMs,
    double RollingAverageInferenceMs,
    double TotalPipelineLatencyMs,
    long ProcessedChunks,
    long BufferUnderruns,
    ComputeBackend ActiveBackend,
    ChannelLevelMeters Levels);

public sealed class DualBusStreamEngine : IAsyncDisposable
{
    private readonly IInferenceEngine _inferenceEngine;
    private readonly WasapiAudioCaptureService _musicCapture;
    private readonly MicrophoneCaptureService _micCapture;
    private readonly WasapiOut _headphoneOut;
    private readonly WasapiOut _streamVirtualOut;
    private readonly BufferedWaveProvider _headphoneOutputBuffer;
    private readonly BufferedWaveProvider _streamOutputBuffer;

    private readonly SampleDelayLine _monitorDelayLine;
    private readonly SampleDelayLine _micDelayLine;
    private readonly AudioRingBuffer _aiInstrumentalRing;

    private readonly int _windowFrames;
    private readonly int _hopFrames;
    private readonly float[] _olaAccumL;
    private readonly float[] _olaAccumR;
    private readonly float[] _olaWeight;
    private readonly float[] _hannWindow;

    private CancellationTokenSource? _cts;
    private Thread? _inferenceThread;
    private Thread? _mixerThread;

    private double _lastInferenceMs;
    private double _avgInferenceMs;
    private long _processedChunks;
    private long _bufferUnderruns;
    private int _manualOffsetSamples;
    private float _targetVocalSuppressionMix = 1.0f;
    private float _currentVocalSuppressionMix = 1.0f;
    private float _targetInstrumentalGain = 1.0f;
    private float _currentInstrumentalGain = 1.0f;
    private float _targetHeadphoneGain = 1.0f;
    private float _currentHeadphoneGain = 1.0f;

    private float _musicPeak;
    private float _aiInstPeak;
    private float _micPeak;
    private float _hpOutPeak;
    private float _streamOutPeak;

    public DualBusStreamEngine(
        IInferenceEngine inferenceEngine,
        WasapiAudioCaptureService musicCapture,
        MicrophoneCaptureService micCapture,
        string? headphoneDeviceId = null,
        string? virtualCableDeviceId = null,
        int windowFrames = 16384)
    {
        _inferenceEngine = inferenceEngine;
        _musicCapture = musicCapture;
        _micCapture = micCapture;
        _windowFrames = windowFrames;
        _hopFrames = windowFrames / 2;

        int maxDelaySamples = WasapiAudioCaptureService.TargetSampleRate * 2 * 2;
        _monitorDelayLine = new SampleDelayLine(maxDelaySamples);
        _micDelayLine = new SampleDelayLine(maxDelaySamples);
        _aiInstrumentalRing = new AudioRingBuffer(maxDelaySamples);

        _olaAccumL = new float[_windowFrames];
        _olaAccumR = new float[_windowFrames];
        _olaWeight = new float[_windowFrames];
        _hannWindow = CreateHannWindow(_windowFrames);

        var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            WasapiAudioCaptureService.TargetSampleRate,
            WasapiAudioCaptureService.TargetChannels);

        _headphoneOutputBuffer = new BufferedWaveProvider(outFormat) { DiscardOnBufferOverflow = true };

        _streamOutputBuffer = new BufferedWaveProvider(outFormat) { DiscardOnBufferOverflow = true };

        using var enumerator = new MMDeviceEnumerator();
        MMDevice hpDevice = string.IsNullOrWhiteSpace(headphoneDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(headphoneDeviceId);

        AudioEndpointInfo? autoVac = WasapiDeviceEnumerator.FindVirtualAudioCableOutput();
        string? targetStreamId = !string.IsNullOrWhiteSpace(virtualCableDeviceId)
            ? virtualCableDeviceId
            : autoVac?.Id;

        MMDevice streamDevice = !string.IsNullOrWhiteSpace(targetStreamId)
            ? enumerator.GetDevice(targetStreamId)
            : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

        _headphoneOut = new WasapiOut(hpDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
        _headphoneOut.Init(_headphoneOutputBuffer);

        _streamVirtualOut = new WasapiOut(streamDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
        _streamVirtualOut.Init(_streamOutputBuffer);
    }

    public void SetManualSyncOffsetMs(double offsetMs)
    {
        int samples = (int)(offsetMs / 1000.0 * WasapiAudioCaptureService.TargetSampleRate * 2);
        Volatile.Write(ref _manualOffsetSamples, samples);
    }

    public void SetVocalSuppressionMix(float mix)
    {
        Volatile.Write(ref _targetVocalSuppressionMix, Math.Clamp(mix, 0f, 1f));
    }

    public void SetInstrumentalGain(float linearGain)
    {
        Volatile.Write(ref _targetInstrumentalGain, Math.Clamp(linearGain, 0f, 2f));
    }

    public void SetHeadphoneGain(float linearGain)
    {
        Volatile.Write(ref _targetHeadphoneGain, Math.Clamp(linearGain, 0f, 2f));
    }

    public LiveTelemetrySnapshot GetTelemetry()
    {
        double avgInf = Volatile.Read(ref _avgInferenceMs);
        double algLatencyMs = (_windowFrames / (double)WasapiAudioCaptureService.TargetSampleRate) * 1000.0;

        var levels = new ChannelLevelMeters(
            MusicInputPeak: Volatile.Read(ref _musicPeak),
            AiInstrumentalPeak: Volatile.Read(ref _aiInstPeak),
            MicInputPeak: Volatile.Read(ref _micPeak),
            HeadphoneOutPeak: Volatile.Read(ref _hpOutPeak),
            StreamOutPeak: Volatile.Read(ref _streamOutPeak));

        return new LiveTelemetrySnapshot(
            LastInferenceMs: Volatile.Read(ref _lastInferenceMs),
            RollingAverageInferenceMs: avgInf,
            TotalPipelineLatencyMs: algLatencyMs + avgInf,
            ProcessedChunks: Interlocked.Read(ref _processedChunks),
            BufferUnderruns: Interlocked.Read(ref _bufferUnderruns),
            ActiveBackend: _inferenceEngine.ActiveBackend,
            Levels: levels);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        int initialDelaySamples = _windowFrames * 2;
        _monitorDelayLine.SetDelaySamples(initialDelaySamples);
        _micDelayLine.SetDelaySamples(initialDelaySamples);

        _musicCapture.Start();
        _micCapture.Start();
        _headphoneOut.Play();
        _streamVirtualOut.Play();

        _inferenceThread = new Thread(() => InferenceWorkerLoop(_cts.Token))
        {
            Name = "ROCm-OLA-InferenceWorker",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _mixerThread = new Thread(() => DualBusMixerLoop(_cts.Token))
        {
            Name = "DualBus-AudioMixer",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _inferenceThread.Start();
        _mixerThread.Start();
    }

    private void InferenceWorkerLoop(CancellationToken token)
    {
        int windowSamples = _windowFrames * 2;
        int hopSamples = _hopFrames * 2;

        float[] slidingWindow = new float[windowSamples];
        float[] newHopBuffer = new float[hopSamples];
        float[] readyHopOut = new float[hopSamples];

        while (!token.IsCancellationRequested)
        {
            if (_musicCapture.InferenceInputRing.AvailableToRead < hopSamples)
            {
                Thread.Sleep(2);
                continue;
            }

            _musicCapture.InferenceInputRing.Read(newHopBuffer);

            // Se o hop inteiro for silêncio digital absoluto, pula inferência GPU e entrega silêncio limpo
            bool isSilentHop = true;
            for (int i = 0; i < hopSamples; i++)
            {
                if (MathF.Abs(newHopBuffer[i]) > 1e-6f)
                {
                    isSilentHop = false;
                    break;
                }
            }

            Array.Copy(slidingWindow, hopSamples, slidingWindow, 0, windowSamples - hopSamples);
            Array.Copy(newHopBuffer, 0, slidingWindow, windowSamples - hopSamples, hopSamples);

            if (isSilentHop)
            {
                Array.Clear(readyHopOut);
                _aiInstrumentalRing.Write(readyHopOut);
                continue;
            }

            var sw = Stopwatch.StartNew();
            SeparationResult result = _inferenceEngine
                .SeparateAsync(slidingWindow, WasapiAudioCaptureService.TargetSampleRate, token)
                .GetAwaiter()
                .GetResult();
            sw.Stop();

            double elapsedMs = sw.Elapsed.TotalMilliseconds;
            Volatile.Write(ref _lastInferenceMs, elapsedMs);
            double prevAvg = Volatile.Read(ref _avgInferenceMs);
            double newAvg = prevAvg <= 0 ? elapsedMs : (prevAvg * 0.9 + elapsedMs * 0.1);
            Volatile.Write(ref _avgInferenceMs, newAvg);
            Interlocked.Increment(ref _processedChunks);

            int inferenceDelaySamples = (int)(newAvg / 1000.0 * WasapiAudioCaptureService.TargetSampleRate) * 2;
            int totalSyncSamples = (_windowFrames * 2) + inferenceDelaySamples + Volatile.Read(ref _manualOffsetSamples);
            _monitorDelayLine.SetDelaySamples(totalSyncSamples);
            _micDelayLine.SetDelaySamples(totalSyncSamples);

            for (int i = 0; i < _windowFrames; i++)
            {
                float w = _hannWindow[i];
                _olaAccumL[i] += result.InstrumentalInterleaved[i * 2] * w;
                _olaAccumR[i] += result.InstrumentalInterleaved[i * 2 + 1] * w;
                _olaWeight[i] += w;
            }

            for (int i = 0; i < _hopFrames; i++)
            {
                float norm = _olaWeight[i] > 1e-6f ? _olaWeight[i] : 1f;
                readyHopOut[i * 2] = _olaAccumL[i] / norm;
                readyHopOut[i * 2 + 1] = _olaAccumR[i] / norm;
            }

            Array.Copy(_olaAccumL, _hopFrames, _olaAccumL, 0, _windowFrames - _hopFrames);
            Array.Copy(_olaAccumR, _hopFrames, _olaAccumR, 0, _windowFrames - _hopFrames);
            Array.Copy(_olaWeight, _hopFrames, _olaWeight, 0, _windowFrames - _hopFrames);
            Array.Clear(_olaAccumL, _windowFrames - _hopFrames, _hopFrames);
            Array.Clear(_olaAccumR, _windowFrames - _hopFrames, _hopFrames);
            Array.Clear(_olaWeight, _windowFrames - _hopFrames, _hopFrames);

            _aiInstrumentalRing.Write(readyHopOut);
        }
    }

    private void DualBusMixerLoop(CancellationToken token)
    {
        const int blockFrames = 480; // exatos 10 ms @ 48 kHz
        const int blockSamples = blockFrames * 2;
        const double blockDurationMs = 10.0;

        float[] rawMusic = new float[blockSamples];
        float[] delayedMonitorMusic = new float[blockSamples];
        float[] hpMixOut = new float[blockSamples];
        float[] aiInstrumental = new float[blockSamples];
        float[] rawMic = new float[blockSamples];
        float[] delayedMic = new float[blockSamples];
        float[] streamMixOut = new float[blockSamples];

        byte[] hpBytes = new byte[blockSamples * sizeof(float)];
        byte[] streamBytes = new byte[blockSamples * sizeof(float)];

        var clock = Stopwatch.StartNew();
        double nextTickMs = clock.Elapsed.TotalMilliseconds;

        while (!token.IsCancellationRequested)
        {
            double nowMs = clock.Elapsed.TotalMilliseconds;
            double waitMs = nextTickMs - nowMs;
            if (waitMs > 1.5)
            {
                Thread.Sleep((int)(waitMs - 1.0));
                continue;
            }
            if (waitMs > 0)
            {
                Thread.SpinWait(200);
                continue;
            }

            nextTickMs += blockDurationMs;
            // Evita acúmulo de ticks caso o sistema tenha sofrido pausa externa
            if (nowMs - nextTickMs > 50.0)
            {
                nextTickMs = nowMs + blockDurationMs;
            }

            // 1. Lê captura de música; se o WASAPI estiver ocioso (sem áudio), injeta silêncio digital (0.0f)
            int musicRead = _musicCapture.MonitorPassThroughRing.Read(rawMusic);
            if (musicRead < blockSamples)
            {
                Array.Clear(rawMusic, musicRead, blockSamples - musicRead);
                // Mantém o InferenceInputRing alimentado com silêncio contínuo durante pausas do WASAPI
                _musicCapture.InferenceInputRing.Write(rawMusic.AsSpan(musicRead, blockSamples - musicRead));
            }
            _monitorDelayLine.ProcessBlock(rawMusic, delayedMonitorMusic);

            // 2. Lê saída do instrumental IA (se ainda estiver preenchendo o 1º chunk, entrega silêncio sem engasgar)
            int aiRead = _aiInstrumentalRing.Read(aiInstrumental);
            if (aiRead < blockSamples)
            {
                Array.Clear(aiInstrumental, aiRead, blockSamples - aiRead);
                if (Interlocked.Read(ref _processedChunks) > 0 && musicRead == blockSamples)
                {
                    Interlocked.Increment(ref _bufferUnderruns);
                }
            }

            // 3. Lê microfone; se ocioso, injeta silêncio limpo
            int micRead = _micCapture.MicRing.Read(rawMic);
            if (micRead < blockSamples)
            {
                Array.Clear(rawMic, micRead, blockSamples - micRead);
            }
            _micDelayLine.ProcessBlock(rawMic, delayedMic);

            float targetWet = Volatile.Read(ref _targetVocalSuppressionMix);
            float targetInstGain = Volatile.Read(ref _targetInstrumentalGain);
            float targetHpGain = Volatile.Read(ref _targetHeadphoneGain);

            float pMusic = 0f, pAi = 0f, pMic = 0f, pHp = 0f, pStream = 0f;
            const float smoothAlpha = 0.002f; // ~50ms crossfade suave em 48kHz stereo

            for (int i = 0; i < blockSamples; i++)
            {
                _currentVocalSuppressionMix += (targetWet - _currentVocalSuppressionMix) * smoothAlpha;
                _currentInstrumentalGain += (targetInstGain - _currentInstrumentalGain) * smoothAlpha;
                _currentHeadphoneGain += (targetHpGain - _currentHeadphoneGain) * smoothAlpha;

                float wet = _currentVocalSuppressionMix;
                float dry = 1.0f - wet;

                float mSample = rawMusic[i];
                float aiSample = aiInstrumental[i];
                float micSample = delayedMic[i];

                float hpSample = SoftKneeLimiter(delayedMonitorMusic[i] * _currentHeadphoneGain);
                hpMixOut[i] = hpSample;

                float musicStem = ((aiSample * wet) + (delayedMonitorMusic[i] * dry)) * _currentInstrumentalGain;
                float stSample = SoftKneeLimiter(musicStem + micSample);
                streamMixOut[i] = stSample;

                float am = MathF.Abs(mSample); if (am > pMusic) pMusic = am;
                float aa = MathF.Abs(aiSample); if (aa > pAi) pAi = aa;
                float amic = MathF.Abs(micSample); if (amic > pMic) pMic = amic;
                float ahp = MathF.Abs(hpSample); if (ahp > pHp) pHp = ahp;
                float ast = MathF.Abs(stSample); if (ast > pStream) pStream = ast;
            }

            Volatile.Write(ref _musicPeak, Math.Clamp(pMusic, 0f, 1f));
            Volatile.Write(ref _aiInstPeak, Math.Clamp(pAi, 0f, 1f));
            Volatile.Write(ref _micPeak, Math.Clamp(pMic, 0f, 1f));
            Volatile.Write(ref _hpOutPeak, Math.Clamp(pHp, 0f, 1f));
            Volatile.Write(ref _streamOutPeak, Math.Clamp(pStream, 0f, 1f));

            // Só empurra bytes se o buffer de saída não estiver atrasado (> 120ms)
            if (_headphoneOutputBuffer.BufferedDuration.TotalMilliseconds < 120)
            {
                Buffer.BlockCopy(hpMixOut, 0, hpBytes, 0, hpBytes.Length);
                _headphoneOutputBuffer.AddSamples(hpBytes, 0, hpBytes.Length);
            }

            if (_streamOutputBuffer.BufferedDuration.TotalMilliseconds < 120)
            {
                Buffer.BlockCopy(streamMixOut, 0, streamBytes, 0, streamBytes.Length);
                _streamOutputBuffer.AddSamples(streamBytes, 0, streamBytes.Length);
            }
        }
    }

    private static float[] CreateHannWindow(int length)
    {
        float[] window = new float[length];
        for (int i = 0; i < length; i++)
        {
            window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (length - 1)));
        }
        return window;
    }

    private static float SoftKneeLimiter(float sample)
    {
        if (sample > 0.95f) return 0.95f + 0.05f * MathF.Tanh((sample - 0.95f) / 0.05f);
        if (sample < -0.95f) return -0.95f + 0.05f * MathF.Tanh((sample + 0.95f) / 0.05f);
        return sample;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _inferenceThread?.Join(500);
            _mixerThread?.Join(500);
            _cts.Dispose();
        }

        _musicCapture.Stop();
        _micCapture.Stop();
        _headphoneOut.Stop();
        _streamVirtualOut.Stop();

        _musicCapture.Dispose();
        _micCapture.Dispose();
        _headphoneOut.Dispose();
        _streamVirtualOut.Dispose();
    }
}