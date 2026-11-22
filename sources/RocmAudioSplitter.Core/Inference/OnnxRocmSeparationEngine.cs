using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RocmAudioSplitter.Core.Dsp;

namespace RocmAudioSplitter.Core.Inference;

public sealed class OnnxRocmSeparationEngine : IInferenceEngine
{
    private readonly GpuHardwareProbeService _probeService;
    private InferenceSession? _session;
    private MdxStftProcessor? _stftProcessor;
    private string _inputTensorName = "input";
    private string _outputTensorName = "output";
    private bool _isInstrumentalModel = true;

    public ComputeBackend ActiveBackend { get; private set; } = ComputeBackend.Cpu;
    public bool IsInitialized => _session is not null;

    public OnnxRocmSeparationEngine(GpuHardwareProbeService probeService)
    {
        _probeService = probeService;
    }

    public async Task InitializeAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("ONNX vocal separation model not found.", modelPath);
        }

        // KARA_2 outputs isolated lead vocals; Inst_HQ_* outputs isolated instrumental
        string fileName = Path.GetFileName(modelPath);
        _isInstrumentalModel = !fileName.Contains("Voc", StringComparison.OrdinalIgnoreCase)
                               && !fileName.Contains("KARA", StringComparison.OrdinalIgnoreCase);

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

        int[] dims = _session.InputMetadata[_inputTensorName].Dimensions;
        int dimF = dims.Length >= 3 && dims[2] > 0 ? dims[2] : 3072;
        int dimT = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 256;

        _stftProcessor = new MdxStftProcessor(dimF, dimT, hopLength: 1024);

        Console.WriteLine($"Model Metadata : {_inputTensorName} [1, 4, {dimF}, {dimT}] | Mode={(_isInstrumentalModel ? "Direct Instrumental" : "Vocal Extraction -> Residual Instrumental")}");

        await RunWarmupPassAsync(cancellationToken);
    }

    private void ConfigureExecutionProvider(SessionOptions options, GpuDeviceInfo gpu)
    {
        if (gpu.IsRocmAvailable)
        {
            try
            {
                var rocmProviderOptions = new Dictionary<string, string>
                {
                    ["device_id"] = "0",
                    ["gpu_mem_limit"] = (2L * 1024 * 1024 * 1024).ToString(),
                    ["arena_extend_strategy"] = "kSameAsRequested"
                };

                options.AppendExecutionProvider("ROCM", rocmProviderOptions);
                ActiveBackend = ComputeBackend.Rocm;
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ROCm provider initialization failed, trying DirectML: {ex.Message}");
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
            if (_session is null || _stftProcessor is null) return;

            var dummyInput = new DenseTensor<float>(new[] { 1, 4, _stftProcessor.DimF, _stftProcessor.DimT });
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
        if (_session is null || _stftProcessor is null)
        {
            throw new InvalidOperationException("Inference engine must be initialized before calling SeparateAsync.");
        }

        return Task.Run(() =>
        {
            ReadOnlySpan<float> src = interleavedStereoPcm.Span;
            int totalFrames = src.Length / 2;

            float[] srcL = new float[totalFrames];
            float[] srcR = new float[totalFrames];
            for (int i = 0; i < totalFrames; i++)
            {
                srcL[i] = src[i * 2];
                srcR[i] = src[i * 2 + 1];
            }

            float[] predL = new float[totalFrames];
            float[] predR = new float[totalFrames];
            float[] weightSum = new float[totalFrames];

            int chunkSize = _stftProcessor.ChunkSamplesPerChannel;
            int hopSize = chunkSize / 2;
            float[] crossfadeWindow = CreateHannWindow(chunkSize);

            float[] chunkInL = new float[chunkSize];
            float[] chunkInR = new float[chunkSize];
            float[] chunkOutL = new float[chunkSize];
            float[] chunkOutR = new float[chunkSize];

            var sw = Stopwatch.StartNew();

            for (int offset = 0; offset < totalFrames; offset += hopSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Array.Clear(chunkInL);
                Array.Clear(chunkInR);
                Array.Clear(chunkOutL);
                Array.Clear(chunkOutR);

                int framesAvailable = Math.Min(chunkSize, totalFrames - offset);
                srcL.AsSpan(offset, framesAvailable).CopyTo(chunkInL);
                srcR.AsSpan(offset, framesAvailable).CopyTo(chunkInR);

                DenseTensor<float> stftInput = _stftProcessor.ForwardStft(chunkInL, chunkInR);
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputTensorName, stftInput)
                };

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputs);
                Tensor<float> stftOutput = outputs.First(o => o.Name == _outputTensorName).AsTensor<float>();

                _stftProcessor.InverseStft(stftOutput, chunkOutL, chunkOutR);

                for (int i = 0; i < framesAvailable; i++)
                {
                    float w = crossfadeWindow[i];
                    predL[offset + i] += chunkOutL[i] * w;
                    predR[offset + i] += chunkOutR[i] * w;
                    weightSum[offset + i] += w;
                }
            }

            float[] instrumental = new float[src.Length];
            float[] vocals = new float[src.Length];

            for (int i = 0; i < totalFrames; i++)
            {
                float norm = weightSum[i] > 1e-6f ? weightSum[i] : 1f;
                float outL = predL[i] / norm;
                float outR = predR[i] / norm;

                if (_isInstrumentalModel)
                {
                    instrumental[i * 2] = outL;
                    instrumental[i * 2 + 1] = outR;
                    vocals[i * 2] = srcL[i] - outL;
                    vocals[i * 2 + 1] = srcR[i] - outR;
                }
                else
                {
                    // KARA_2 outputs isolated lead vocals -> subtract from original for karaoke backing track
                    vocals[i * 2] = outL;
                    vocals[i * 2 + 1] = outR;
                    instrumental[i * 2] = srcL[i] - outL;
                    instrumental[i * 2 + 1] = srcR[i] - outR;
                }
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