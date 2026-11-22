# Run inside: C:\Users\bruno\Downloads\sources-20261117T114549Z-1-001\sources
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

New-Item -ItemType Directory -Force -Path "RocmAudioSplitter.Core\Inference", "RocmAudioSplitter.Core\Audio", "RocmAudioSplitter.Core\Dsp", "RocmAudioSplitter.Cli" | Out-Null

# 1. RocmAudioSplitter.sln[cite: 4]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.sln", @'
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.14.36109.1
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "RocmAudioSplitter.Core", "RocmAudioSplitter.Core\RocmAudioSplitter.Core.csproj", "{B1F4C8A2-4D9E-4B1C-8A2F-9E1D3C5B7A01}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "RocmAudioSplitter.Cli", "RocmAudioSplitter.Cli\RocmAudioSplitter.Cli.csproj", "{C2E5D9B3-5E0F-4C2D-9B3A-0F2E4D6C8B02}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{B1F4C8A2-4D9E-4B1C-8A2F-9E1D3C5B7A01}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B1F4C8A2-4D9E-4B1C-8A2F-9E1D3C5B7A01}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B1F4C8A2-4D9E-4B1C-8A2F-9E1D3C5B7A01}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B1F4C8A2-4D9E-4B1C-8A2F-9E1D3C5B7A01}.Release|Any CPU.Build.0 = Release|Any CPU
		{C2E5D9B3-5E0F-4C2D-9B3A-0F2E4D6C8B02}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{C2E5D9B3-5E0F-4C2D-9B3A-0F2E4D6C8B02}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{C2E5D9B3-5E0F-4C2D-9B3A-0F2E4D6C8B02}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{C2E5D9B3-5E0F-4C2D-9B3A-0F2E4D6C8B02}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
'@, $utf8NoBom)

# 2. RocmAudioSplitter.Core.csproj[cite: 5]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Core\RocmAudioSplitter.Core.csproj", @'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime.Managed" Version="1.21.0" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.21.0" />
    <PackageReference Include="NAudio" Version="2.2.1" />
    <PackageReference Include="System.Management" Version="10.0.0" />
  </ItemGroup>

</Project>
'@, $utf8NoBom)

# 3. RocmAudioSplitter.Cli.csproj[cite: 10]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Cli\RocmAudioSplitter.Cli.csproj", @'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\RocmAudioSplitter.Core\RocmAudioSplitter.Core.csproj" />
  </ItemGroup>

</Project>
'@, $utf8NoBom)

# 4. IInferenceEngine.cs[cite: 7]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Core\Inference\IInferenceEngine.cs", @'
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
'@, $utf8NoBom)

# 5. GpuHardwareProbeService.cs[cite: 12]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Core\Inference\GpuHardwareProbeService.cs", @'
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RocmAudioSplitter.Core.Inference;

public sealed class GpuHardwareProbeService
{
    public GpuDeviceInfo ProbeSystemGpu()
    {
        string gpuName = "Unknown GPU";
        long vramBytes = 0;

        if (OperatingSystem.IsWindows())
        {
            (gpuName, vramBytes) = QueryWindowsGpuWmi();
        }

        bool isAmd = gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                     || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase);

        string arch = DetectArchitecture(gpuName);
        bool rocmAvailable = CheckRocmRuntimeLibraries(out string rocmDetails);
        bool directMlAvailable = OperatingSystem.IsWindows();

        return new GpuDeviceInfo(
            Name: gpuName,
            Architecture: arch,
            DedicatedVramBytes: vramBytes,
            IsRocmAvailable: isAmd && rocmAvailable,
            IsDirectMlAvailable: directMlAvailable,
            RuntimeDetails: rocmDetails);
    }

    private static bool CheckRocmRuntimeLibraries(out string details)
    {
        string? hipPath = Environment.GetEnvironmentVariable("HIP_PATH");
        string[] candidateDlls = OperatingSystem.IsWindows()
            ? ["amdhip64_7.dll", "amdhip64_6.dll", "amdhip64.dll", "onnxruntime_providers_rocm.dll"]
            : ["libamdhip64.so", "libonnxruntime_providers_rocm.so"];

        foreach (string dll in candidateDlls)
        {
            if (NativeLibrary.TryLoad(dll, out nint handle))
            {
                NativeLibrary.Free(handle);
                details = $"Loaded {dll} (HIP_PATH={hipPath ?? "system PATH"})";
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(hipPath) && Directory.Exists(hipPath))
        {
            details = $"HIP_PATH found at '{hipPath}', but ROCm provider DLLs were not found on PATH.";
            return false;
        }

        details = "ROCm/HIP runtime libraries not detected in PATH; DirectML fallback ready.";
        return false;
    }

    private static string DetectArchitecture(string gpuName)
    {
        if (gpuName.Contains("9060", StringComparison.OrdinalIgnoreCase)
            || gpuName.Contains("9070", StringComparison.OrdinalIgnoreCase))
        {
            return "RDNA 4 (gfx12xx)";
        }

        if (gpuName.Contains("7900", StringComparison.OrdinalIgnoreCase)
            || gpuName.Contains("7800", StringComparison.OrdinalIgnoreCase)
            || gpuName.Contains("7700", StringComparison.OrdinalIgnoreCase)
            || gpuName.Contains("7600", StringComparison.OrdinalIgnoreCase))
        {
            return "RDNA 3 (gfx11xx)";
        }

        return "Generic / Unknown";
    }

    [SupportedOSPlatform("windows")]
    private static (string Name, long VramBytes) QueryWindowsGpuWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Unknown GPU";
                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    long ram = obj["AdapterRAM"] is uint u ? u : 0L;
                    return (name, ram);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WMI GPU query failed: {ex.Message}");
        }

        return ("No AMD Radeon GPU detected", 0);
    }
}
'@, $utf8NoBom)

# 6. OnnxRocmSeparationEngine.cs[cite: 6]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Core\Inference\OnnxRocmSeparationEngine.cs", @'
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RocmAudioSplitter.Core.Inference;

public sealed class OnnxRocmSeparationEngine : IInferenceEngine
{
    private readonly GpuHardwareProbeService _probeService;
    private readonly int _chunkSamplesPerChannel;
    private InferenceSession? _session;
    private string _inputTensorName = "input";
    private string _outputTensorName = "output";

    public ComputeBackend ActiveBackend { get; private set; } = ComputeBackend.Cpu;
    public bool IsInitialized => _session is not null;

    public OnnxRocmSeparationEngine(
        GpuHardwareProbeService probeService,
        int chunkSamplesPerChannel = 262144)
    {
        _probeService = probeService;
        _chunkSamplesPerChannel = chunkSamplesPerChannel;
    }

    public async Task InitializeAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("ONNX vocal separation model not found.", modelPath);
        }

        GpuDeviceInfo gpu = _probeService.ProbeSystemGpu();
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = true
        };

        ConfigureExecutionProvider(options, gpu);

        _session = await Task.Run(() => new InferenceSession(modelPath, options), cancellationToken);
        _inputTensorName = _session.InputMetadata.Keys.First();
        _outputTensorName = _session.OutputMetadata.Keys.First();

        await RunWarmupPassAsync(cancellationToken);
    }

    private void ConfigureExecutionProvider(SessionOptions options, GpuDeviceInfo gpu)
    {
        if (gpu.IsRocmAvailable)
        {
            try
            {
                var rocmOptions = new OrtROCMProviderOptions();
                rocmOptions.UpdateOptions(new Dictionary<string, string>
                {
                    ["device_id"] = "0",
                    ["gpu_mem_limit"] = (2L * 1024 * 1024 * 1024).ToString(),
                    ["arena_extend_strategy"] = "kSameAsRequested"
                });

                options.AppendExecutionProvider_ROCM(rocmOptions);
                ActiveBackend = ComputeBackend.Rocm;
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ROCm provider initialization failed, falling back to DirectML: {ex.Message}");
            }
        }

        if (gpu.IsDirectMlAvailable && OperatingSystem.IsWindows())
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
                ActiveBackend = ComputeBackend.DirectML;
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DirectML provider initialization failed, falling back to CPU: {ex.Message}");
            }
        }

        ActiveBackend = ComputeBackend.Cpu;
    }

    private Task RunWarmupPassAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (_session is null) return;

            var dummyInput = new DenseTensor<float>(new[] { 1, 2, _chunkSamplesPerChannel });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputTensorName, dummyInput)
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = _session.Run(inputs);
        }, cancellationToken);
    }

    public Task<SeparationResult> SeparateAsync(
        ReadOnlyMemory<float> interleavedStereoPcm,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            throw new InvalidOperationException("Inference engine must be initialized before calling SeparateAsync.");
        }

        return Task.Run(() =>
        {
            ReadOnlySpan<float> src = interleavedStereoPcm.Span;
            int totalFrames = src.Length / 2;
            float[] instrumental = new float[src.Length];
            float[] vocals = new float[src.Length];

            var sw = Stopwatch.StartNew();

            int hopSize = _chunkSamplesPerChannel / 2;
            float[] hannWindow = CreateHannWindow(_chunkSamplesPerChannel);

            for (int offset = 0; offset < totalFrames; offset += hopSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int framesAvailable = Math.Min(_chunkSamplesPerChannel, totalFrames - offset);
                var inputTensor = new DenseTensor<float>(new[] { 1, 2, _chunkSamplesPerChannel });

                for (int i = 0; i < framesAvailable; i++)
                {
                    float w = hannWindow[i];
                    int srcIdx = (offset + i) * 2;
                    inputTensor[0, 0, i] = src[srcIdx] * w;
                    inputTensor[0, 1, i] = src[srcIdx + 1] * w;
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputTensorName, inputTensor)
                };

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputs);
                Tensor<float> predictedInstrumental = outputs.First(o => o.Name == _outputTensorName).AsTensor<float>();

                for (int i = 0; i < framesAvailable; i++)
                {
                    int dstIdx = (offset + i) * 2;
                    float instL = predictedInstrumental[0, 0, i];
                    float instR = predictedInstrumental[0, 1, i];

                    instrumental[dstIdx] += instL;
                    instrumental[dstIdx + 1] += instR;
                }
            }

            for (int i = 0; i < src.Length; i++)
            {
                vocals[i] = src[i] - instrumental[i];
            }

            sw.Stop();

            TimeSpan audioDuration = TimeSpan.FromSeconds((double)totalFrames / sampleRate);
            var benchmark = new SeparationBenchmark(audioDuration, sw.Elapsed, ActiveBackend);

            return new SeparationResult(instrumental, vocals, sampleRate, 2, benchmark);
        }, cancellationToken);
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

    public ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _session = null;
        return ValueTask.CompletedTask;
    }
}
'@, $utf8NoBom)

# 7. OfflineStemSeparatorService.cs[cite: 8]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Core\Audio\OfflineStemSeparatorService.cs", @'
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RocmAudioSplitter.Core.Inference;

namespace RocmAudioSplitter.Core.Audio;

public sealed class OfflineStemSeparatorService
{
    private readonly IInferenceEngine _inferenceEngine;

    public OfflineStemSeparatorService(IInferenceEngine inferenceEngine)
    {
        _inferenceEngine = inferenceEngine;
    }

    public async Task<SeparationBenchmark> ProcessFileAsync(
        string inputAudioPath,
        string outputDirectory,
        int targetSampleRate = 44100,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        string baseName = Path.GetFileNameWithoutExtension(inputAudioPath);

        float[] interleavedPcm = DecodeAndResampleToStereoFloat(inputAudioPath, targetSampleRate);

        SeparationResult result = await _inferenceEngine.SeparateAsync(
            interleavedPcm,
            targetSampleRate,
            cancellationToken);

        string instrumentalPath = Path.Combine(outputDirectory, $"{baseName}_instrumental.wav");
        string vocalsPath = Path.Combine(outputDirectory, $"{baseName}_vocals.wav");

        WriteWav24Bit(instrumentalPath, result.InstrumentalInterleaved, targetSampleRate, 2);
        WriteWav24Bit(vocalsPath, result.VocalsInterleaved, targetSampleRate, 2);

        return result.Benchmark;
    }

    private static float[] DecodeAndResampleToStereoFloat(string filePath, int targetSampleRate)
    {
        using var reader = new AudioFileReader(filePath);
        ISampleProvider provider = reader;

        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        var samples = new List<float>((int)(reader.Length / sizeof(float)));
        float[] readBuffer = new float[targetSampleRate * 2];
        int read;

        while ((read = provider.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            samples.AddRange(readBuffer.AsSpan(0, read));
        }

        return samples.ToArray();
    }

    private static void WriteWav24Bit(string outputPath, float[] interleavedSamples, int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        using var writer = new WaveFileWriter(outputPath, format);
        writer.WriteSamples(interleavedSamples, 0, interleavedSamples.Length);
    }
}
'@, $utf8NoBom)

# 8. AudioRingBuffer.cs[cite: 9]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Core\Dsp\AudioRingBuffer.cs", @'
using System.Runtime.CompilerServices;

namespace RocmAudioSplitter.Core.Dsp;

public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private int _writeIndex;
    private int _readIndex;

    public int Capacity => _capacity;

    public AudioRingBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySamples);
        _capacity = capacitySamples;
        _buffer = GC.AllocateArray<float>(capacitySamples, pinned: true);
    }

    public int AvailableToRead
    {
        get
        {
            int write = Volatile.Read(ref _writeIndex);
            int read = Volatile.Read(ref _readIndex);
            return write >= read ? write - read : _capacity - read + write;
        }
    }

    public int AvailableToWrite => _capacity - 1 - AvailableToRead;

    public int Write(ReadOnlySpan<float> source)
    {
        int writable = Math.Min(source.Length, AvailableToWrite);
        int write = Volatile.Read(ref _writeIndex);

        for (int i = 0; i < writable; i++)
        {
            _buffer[write] = source[i];
            write = NextIndex(write);
        }

        Volatile.Write(ref _writeIndex, write);
        return writable;
    }

    public int Read(Span<float> destination)
    {
        int readable = Math.Min(destination.Length, AvailableToRead);
        int read = Volatile.Read(ref _readIndex);

        for (int i = 0; i < readable; i++)
        {
            destination[i] = _buffer[read];
            read = NextIndex(read);
        }

        Volatile.Write(ref _readIndex, read);
        return readable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextIndex(int index)
    {
        int next = index + 1;
        return next == _capacity ? 0 : next;
    }
}
'@, $utf8NoBom)

# 9. Program.cs[cite: 11]
[System.IO.File]::WriteAllText("$PWD\RocmAudioSplitter.Cli\Program.cs", @'
using RocmAudioSplitter.Core.Audio;
using RocmAudioSplitter.Core.Inference;

Console.WriteLine("=== ROCm Audio Splitter - Milestone 1 Harness (.NET 10) ===");

var gpuProbe = new GpuHardwareProbeService();
GpuDeviceInfo gpuInfo = gpuProbe.ProbeSystemGpu();

Console.WriteLine($"Detected GPU   : {gpuInfo.Name} ({gpuInfo.Architecture})");
Console.WriteLine($"Dedicated VRAM : {gpuInfo.DedicatedVramBytes / (1024 * 1024)} MB");
Console.WriteLine($"ROCm Status    : {(gpuInfo.IsRocmAvailable ? "READY" : "NOT DETECTED")} - {gpuInfo.RuntimeDetails}");

if (args.Length < 2)
{
    Console.WriteLine();
    Console.WriteLine("Usage: RocmAudioSplitter.Cli <path-to-model.onnx> <path-to-song.mp3|wav|flac> [output-dir]");
    return;
}

string modelPath = args[0];
string inputAudioPath = args[1];
string outputDir = args.Length >= 3 ? args[2] : Path.Combine(Environment.CurrentDirectory, "stems_out");

await using var engine = new OnnxRocmSeparationEngine(gpuProbe);
Console.WriteLine("\nInitializing ONNX Runtime session and running GPU warm-up pass...");
await engine.InitializeAsync(modelPath);
Console.WriteLine($"Active Compute Backend: {engine.ActiveBackend}");

var separator = new OfflineStemSeparatorService(engine);
Console.WriteLine($"Processing '{Path.GetFileName(inputAudioPath)}'...");

SeparationBenchmark benchmark = await separator.ProcessFileAsync(inputAudioPath, outputDir);

Console.WriteLine("\n=== Separation Complete ===");
Console.WriteLine($"Output Folder   : {outputDir}");
Console.WriteLine($"Audio Duration  : {benchmark.AudioDuration:mm\:ss\.ff}");
Console.WriteLine($"GPU Time        : {benchmark.InferenceDuration:mm\:ss\.ff}");
Console.WriteLine($"Real-Time Factor: {benchmark.RealTimeFactor:F3}x (Target < 0.300x)");
'@, $utf8NoBom)

# Clean up any leftover .docx files exported by Google Drive
Get-ChildItem -Recurse -Filter "*.docx" | Remove-Item -Force