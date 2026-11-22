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