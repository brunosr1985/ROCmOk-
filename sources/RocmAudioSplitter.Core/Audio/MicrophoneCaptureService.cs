using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RocmAudioSplitter.Core.Dsp;

namespace RocmAudioSplitter.Core.Audio;

public sealed class MicrophoneCaptureService : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _rawBuffer;
    private readonly ISampleProvider _convertedProvider;
    private readonly BiQuadFilter _highPassLeft;
    private readonly BiQuadFilter _highPassRight;
    private readonly float[] _scratch = new float[WasapiAudioCaptureService.TargetSampleRate * 2];
    private float _targetGainLinear = 1.0f;
    private float _currentGainLinear = 1.0f;
    private bool _isMuted;

    public AudioRingBuffer MicRing { get; }
    public string DeviceName { get; }
    public bool IsMuted => Volatile.Read(ref _isMuted);

    public MicrophoneCaptureService(string? captureDeviceId = null, int ringCapacitySeconds = 4)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = string.IsNullOrWhiteSpace(captureDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(captureDeviceId);

        DeviceName = device.FriendlyName;
        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 10);

        int ringSamples = WasapiAudioCaptureService.TargetSampleRate * 2 * ringCapacitySeconds;
        MicRing = new AudioRingBuffer(ringSamples);

        _rawBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        ISampleProvider pipeline = _rawBuffer.ToSampleProvider();
        if (pipeline.WaveFormat.Channels == 1)
        {
            pipeline = new MonoToStereoSampleProvider(pipeline);
        }
        if (pipeline.WaveFormat.SampleRate != WasapiAudioCaptureService.TargetSampleRate)
        {
            pipeline = new WdlResamplingSampleProvider(pipeline, WasapiAudioCaptureService.TargetSampleRate);
        }

        _convertedProvider = pipeline;
        _highPassLeft = BiQuadFilter.HighPassFilter(WasapiAudioCaptureService.TargetSampleRate, 80f, 0.707f);
        _highPassRight = BiQuadFilter.HighPassFilter(WasapiAudioCaptureService.TargetSampleRate, 80f, 0.707f);

        _capture.DataAvailable += OnDataAvailable;
    }

    public void SetGainDb(float gainDb)
    {
        float linear = MathF.Pow(10f, gainDb / 20f);
        Volatile.Write(ref _targetGainLinear, linear);
    }

    public void SetMuted(bool muted)
    {
        Volatile.Write(ref _isMuted, muted);
    }

    public void Start() => _capture.StartRecording();

    public void Stop() => _capture.StopRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        _rawBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        float targetGain = Volatile.Read(ref _isMuted) ? 0f : Volatile.Read(ref _targetGainLinear);

        int read;
        while ((read = _convertedProvider.Read(_scratch.AsSpan())) > 0)
        {
            for (int i = 0; i + 1 < read; i += 2)
            {
                // Interpolação suave por frame evita pop ao mutar/desmutar via hotkey
                _currentGainLinear += (targetGain - _currentGainLinear) * 0.004f;
                _scratch[i] = _highPassLeft.Transform(_scratch[i]) * _currentGainLinear;
                _scratch[i + 1] = _highPassRight.Transform(_scratch[i + 1]) * _currentGainLinear;
            }

            MicRing.Write(_scratch.AsSpan(0, read));
        }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
    }
}