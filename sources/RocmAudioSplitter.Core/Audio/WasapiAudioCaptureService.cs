using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RocmAudioSplitter.Core.Dsp;

namespace RocmAudioSplitter.Core.Audio;

/// <summary>
/// Captura o áudio de reprodução do Windows via WASAPI Loopback (sistema inteiro)
/// OU isolado por processo (PID específico via PROCESS_LOOPBACK_MODE) e padroniza
/// em 48 kHz Stereo Float32 dentro de AudioRingBuffers.
/// </summary>
public sealed class WasapiAudioCaptureService : IDisposable
{
    public const int TargetSampleRate = 48000;
    public const int TargetChannels = 2;

    private readonly WasapiLoopbackCapture? _systemCapture;
    private readonly ProcessLoopbackCapture? _processCapture;
    private readonly BufferedWaveProvider _rawBuffer;
    private readonly ISampleProvider _convertedProvider;
    private readonly float[] _conversionScratch = new float[TargetSampleRate * TargetChannels];

    public AudioRingBuffer InferenceInputRing { get; }
    public AudioRingBuffer MonitorPassThroughRing { get; }
    public string SourceDescription { get; }
    public bool IsProcessIsolated => _processCapture is not null;

    public WasapiAudioCaptureService(
        string? playbackDeviceId = null,
        int? targetProcessId = null,
        int ringCapacitySeconds = 4)
    {
        int ringSamples = TargetSampleRate * TargetChannels * ringCapacitySeconds;
        InferenceInputRing = new AudioRingBuffer(ringSamples);
        MonitorPassThroughRing = new AudioRingBuffer(ringSamples);

        WaveFormat captureFormat;

        if (targetProcessId is int pid && pid > 0 && OperatingSystem.IsWindows())
        {
            string procLabel = $"PID {pid}";
            try
            {
                using var proc = Process.GetProcessById(pid);
                procLabel = $"{proc.ProcessName} (PID {pid})";
            }
            catch { }

            SourceDescription = $"Process Isolated: {procLabel}";
            _processCapture = new ProcessLoopbackCapture(pid, includeProcessTree: true);
            captureFormat = _processCapture.WaveFormat;
            _processCapture.DataAvailable += OnDataAvailable;
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device = string.IsNullOrWhiteSpace(playbackDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(playbackDeviceId);

            SourceDescription = $"System Device: {device.FriendlyName}";
            _systemCapture = new WasapiLoopbackCapture(device);
            captureFormat = _systemCapture.WaveFormat;
            _systemCapture.DataAvailable += OnDataAvailable;
        }

        _rawBuffer = new BufferedWaveProvider(captureFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        ISampleProvider pipeline = _rawBuffer.ToSampleProvider();
        if (pipeline.WaveFormat.Channels == 1)
        {
            pipeline = new MonoToStereoSampleProvider(pipeline);
        }
        if (pipeline.WaveFormat.SampleRate != TargetSampleRate)
        {
            pipeline = new WdlResamplingSampleProvider(pipeline, TargetSampleRate);
        }

        _convertedProvider = pipeline;
    }

    public void Start()
    {
        if (_processCapture is not null)
            _processCapture.StartRecording();
        else
            _systemCapture?.StartRecording();
    }

    public void Stop()
    {
        if (_processCapture is not null)
            _processCapture.StopRecording();
        else
            _systemCapture?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        _rawBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        int samplesRead;
        while ((samplesRead = _convertedProvider.Read(_conversionScratch.AsSpan())) > 0)
        {
            ReadOnlySpan<float> span = _conversionScratch.AsSpan(0, samplesRead);
            InferenceInputRing.Write(span);
            MonitorPassThroughRing.Write(span);
        }
    }

    public void Dispose()
    {
        if (_processCapture is not null)
        {
            _processCapture.DataAvailable -= OnDataAvailable;
            _processCapture.Dispose();
        }
        if (_systemCapture is not null)
        {
            _systemCapture.DataAvailable -= OnDataAvailable;
            _systemCapture.Dispose();
        }
    }
}