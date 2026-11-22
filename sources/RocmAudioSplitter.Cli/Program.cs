using RocmAudioSplitter.Core.Audio;
using RocmAudioSplitter.Core.Inference;
using RocmAudioSplitter.Core.Streaming;

Console.WriteLine("=== ROCm Audio Splitter (.NET 10) - M1 Offline & M2+M3 Live Engine ===");

var gpuProbe = new GpuHardwareProbeService();
GpuDeviceInfo gpuInfo = gpuProbe.ProbeSystemGpu();

Console.WriteLine($"Detected GPU   : {gpuInfo.Name} ({gpuInfo.Architecture})");
Console.WriteLine($"Dedicated VRAM : {gpuInfo.DedicatedVramBytes / (1024 * 1024)} MB");
Console.WriteLine($"ROCm Status    : {(gpuInfo.IsRocmAvailable ? "READY" : "NOT DETECTED")} - {gpuInfo.RuntimeDetails}");

if (args.Length == 0)
{
    Console.WriteLine("\nActive Audio Applications (Process Isolation Ready):");
    var activeApps = AudioProcessEnumerator.GetActiveAudioProcesses();
    if (activeApps.Count == 0)
    {
        Console.WriteLine("    (No active audio sessions detected right now — play audio in Spotify/Chrome and re-run)");
    }
    else
    {
        foreach (var app in activeApps)
        {
            Console.WriteLine($"    - {app.DisplayLabel}");
        }
    }

    Console.WriteLine("\nAvailable Audio Devices:");
    Console.WriteLine("  [Playback / Headphones / Virtual Cables]");
    foreach (var dev in WasapiDeviceEnumerator.GetPlaybackDevices())
    {
        Console.WriteLine($"    - {dev.FriendlyName} {(dev.IsDefault ? "(Default)" : "")} {(dev.IsVirtualCable ? "[VIRTUAL CABLE DETECTED]" : "")}");
    }

    Console.WriteLine("  [Microphones]");
    foreach (var dev in WasapiDeviceEnumerator.GetCaptureDevices())
    {
        Console.WriteLine($"    - {dev.FriendlyName} {(dev.IsDefault ? "(Default)" : "")}");
    }

    Console.WriteLine("\nUsage:");
    Console.WriteLine("  Offline Mode      : RocmAudioSplitter.Cli <model.onnx> <song.mp3|wav|flac> [output-dir]");
    Console.WriteLine("  Live System Mode  : RocmAudioSplitter.Cli --live <model.onnx>");
    Console.WriteLine("  Live Process Mode : RocmAudioSplitter.Cli --live <model.onnx> --app spotify");
    Console.WriteLine("  Live PID Mode     : RocmAudioSplitter.Cli --live <model.onnx> --pid 12345");
    return;
}

if (args[0].Equals("--live", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2)
    {
        Console.WriteLine("Error: Specify the ONNX model path. Example: RocmAudioSplitter.Cli --live C:\\models\\UVR_MDXNET_KARA_2.onnx --app spotify");
        return;
    }

    string liveModelPath = args[1];
    int? targetPid = ResolveTargetProcessId(args);

    await using var liveEngine = new OnnxRocmSeparationEngine(gpuProbe);
    Console.WriteLine("\nInitializing ROCm/ONNX engine for real-time streaming...");
    await liveEngine.InitializeAsync(liveModelPath);
    Console.WriteLine($"Active Compute Backend: {liveEngine.ActiveBackend}");

    var musicCapture = new WasapiAudioCaptureService(targetProcessId: targetPid);
    var micCapture = new MicrophoneCaptureService();
    var virtualCable = WasapiDeviceEnumerator.FindVirtualAudioCableOutput();

    Console.WriteLine($"Audio Source   : {musicCapture.SourceDescription}");
    Console.WriteLine($"Mic Capture    : {micCapture.DeviceName}");
    Console.WriteLine($"Stream Output  : {virtualCable?.FriendlyName ?? "Default Communications Device (Install VB-Cable for dedicated OBS bus)"}");

    await using var streamEngine = new DualBusStreamEngine(liveEngine, musicCapture, micCapture);
    streamEngine.Start();

    Console.WriteLine("\n[LIVE STREAMING ACTIVE] Press ENTER to stop...");
    while (!Console.KeyAvailable)
    {
        await Task.Delay(1000);
        LiveTelemetrySnapshot stats = streamEngine.GetTelemetry();
        Console.Write($"\rChunks: {stats.ProcessedChunks} | GPU Infer: {stats.LastInferenceMs:F1}ms (avg {stats.RollingAverageInferenceMs:F1}ms) | Total Sync Delay: {stats.TotalPipelineLatencyMs:F0}ms | Underruns: {stats.BufferUnderruns}   ");
    }

    Console.ReadLine();
    Console.WriteLine("\nStopping live audio engine...");
    return;
}

if (args.Length < 2)
{
    Console.WriteLine("Usage: RocmAudioSplitter.Cli <model.onnx> <song.mp3|wav|flac> [output-dir]");
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
Console.WriteLine($"Audio Duration  : {benchmark.AudioDuration.TotalSeconds:F2}s");
Console.WriteLine($"GPU Time        : {benchmark.InferenceDuration.TotalSeconds:F2}s");
Console.WriteLine($"Real-Time Factor: {benchmark.RealTimeFactor:F3}x (Target < 0.300x)");

static int? ResolveTargetProcessId(string[] cliArgs)
{
    for (int i = 2; i < cliArgs.Length - 1; i++)
    {
        if (cliArgs[i].Equals("--pid", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(cliArgs[i + 1], out int parsedPid))
        {
            return parsedPid;
        }

        if (cliArgs[i].Equals("--app", StringComparison.OrdinalIgnoreCase))
        {
            string appQuery = cliArgs[i + 1];
            var match = AudioProcessEnumerator.GetActiveAudioProcesses()
                .FirstOrDefault(p => p.ProcessName.Contains(appQuery, StringComparison.OrdinalIgnoreCase)
                                  || p.WindowTitle.Contains(appQuery, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                Console.WriteLine($"Matched Process: {match.DisplayLabel}");
                return match.ProcessId;
            }

            Console.WriteLine($"Warning: No active audio session matched '{appQuery}'. Falling back to system loopback.");
        }
    }

    return null;
}