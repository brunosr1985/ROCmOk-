namespace RocmAudioSplitter.Core.Inference;

public enum ComputeBackend
{
    Rocm,
    MIGraphX,
    DirectML,
    Cpu
}

public sealed record GpuDeviceInfo(
    string Name,
    string Architecture,
    long DedicatedVramBytes,
    bool IsRocmAvailable,
    bool IsDirectMlAvailable,
    string RuntimeDetails);

public sealed record SeparationBenchmark(
    TimeSpan AudioDuration,
    TimeSpan InferenceDuration,
    ComputeBackend ActiveBackend)
{
    public double RealTimeFactor => AudioDuration.TotalSeconds > 0
        ? InferenceDuration.TotalSeconds / AudioDuration.TotalSeconds
        : 0.0;
}

public sealed record SeparationResult(
    float[] InstrumentalInterleaved,
    float[] VocalsInterleaved,
    int SampleRate,
    int Channels,
    SeparationBenchmark Benchmark);

public interface IInferenceEngine : IAsyncDisposable
{
    ComputeBackend ActiveBackend { get; }
    bool IsInitialized { get; }

    Task InitializeAsync(string modelPath, CancellationToken cancellationToken = default);

    Task<SeparationResult> SeparateAsync(
        ReadOnlyMemory<float> interleavedStereoPcm,
        int sampleRate,
        CancellationToken cancellationToken = default);
}