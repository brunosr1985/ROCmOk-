using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RocmAudioSplitter.Core.Inference;

public sealed class GpuHardwareProbeService
{
    public GpuDeviceInfo ProbeSystemGpu()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProbeLinuxRocmFallback();
        }

        (string gpuName, long vramBytes) = ReadPrimaryDisplayAdapterFromRegistry();
        string arch = InferAmdArchitecture(gpuName);
        (bool rocmFound, string rocmDetails) = DetectInstalledRocmRuntime();

        return new GpuDeviceInfo(
            Name: gpuName,
            Architecture: arch,
            DedicatedVramBytes: vramBytes,
            IsRocmAvailable: rocmFound,
            IsDirectMlAvailable: File.Exists(Path.Combine(Environment.SystemDirectory, "DirectML.dll")),
            RuntimeDetails: rocmDetails);
    }

    [SupportedOSPlatform("windows")]
    private static (string Name, long VramBytes) ReadPrimaryDisplayAdapterFromRegistry()
    {
        const string displayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        string bestName = "Unknown GPU";
        long bestVram = 0;

        try
        {
            using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(displayClassKey);
            if (baseKey is null) return (bestName, bestVram);

            foreach (string subKeyName in baseKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;
                using RegistryKey? adapterKey = baseKey.OpenSubKey(subKeyName);
                if (adapterKey is null) continue;

                string? desc = adapterKey.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrWhiteSpace(desc)) continue;

                long vram = 0;
                object? qwMem = adapterKey.GetValue("HardwareInformation.qwMemorySize");
                if (qwMem is long lVal) vram = lVal;
                else if (qwMem is byte[] bytes && bytes.Length >= 8) vram = BitConverter.ToInt64(bytes, 0);

                // Prioriza GPU discreta AMD Radeon ou maior VRAM dedicada
                bool isAmd = desc.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                          || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase);

                if ((isAmd && !bestName.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) || vram > bestVram)
                {
                    bestName = desc;
                    bestVram = vram;
                }
            }
        }
        catch
        {
            // Fallback silencioso caso política restrinja leitura de registro
        }

        return (bestName, bestVram);
    }

    private static (bool Found, string Details) DetectInstalledRocmRuntime()
    {
        var candidateDirs = new List<string>();

        string? hipPath = Environment.GetEnvironmentVariable("HIP_PATH");
        if (!string.IsNullOrWhiteSpace(hipPath))
            candidateDirs.Add(Path.Combine(hipPath, "bin"));

        string? rocmPath = Environment.GetEnvironmentVariable("ROCM_PATH");
        if (!string.IsNullOrWhiteSpace(rocmPath))
            candidateDirs.Add(Path.Combine(rocmPath, "bin"));

        // Procura instalações padrão do AMD ROCm / HIP SDK em Program Files
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string amdRocmRoot = Path.Combine(programFiles, "AMD", "ROCm");
        if (Directory.Exists(amdRocmRoot))
        {
            foreach (string verDir in Directory.EnumerateDirectories(amdRocmRoot).OrderByDescending(d => d))
            {
                candidateDirs.Add(Path.Combine(verDir, "bin"));
            }
        }

        candidateDirs.Add(Environment.SystemDirectory);

        foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;

            // Detecta dinamicamente qualquer major version (amdhip64_7.dll, amdhip64_10.dll, etc.)
            string? hipDll = Directory.EnumerateFiles(dir, "amdhip64*.dll", SearchOption.TopDirectoryOnly)
                                      .OrderByDescending(f => f)
                                      .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(hipDll))
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(hipDll);
                string fileVer = !string.IsNullOrWhiteSpace(fvi.ProductVersion)
                    ? fvi.ProductVersion
                    : (!string.IsNullOrWhiteSpace(fvi.FileVersion) ? fvi.FileVersion : "Detected");

                return (true, $"HIP Runtime {fileVer} ({Path.GetFileName(hipDll)})");
            }
        }

        return (false, "DirectML / MIGraphX Fallback Active");
    }

    private static string InferAmdArchitecture(string gpuName)
    {
        if (gpuName.Contains("RX 90", StringComparison.OrdinalIgnoreCase)) return "RDNA 4 / gfx12xx";
        if (gpuName.Contains("RX 7", StringComparison.OrdinalIgnoreCase)) return "RDNA 3 / gfx11xx";
        if (gpuName.Contains("RX 6", StringComparison.OrdinalIgnoreCase)) return "RDNA 2 / gfx103x";
        if (gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD Radeon GPU";
        return "DirectX 12 Compatible GPU";
    }

    private static GpuDeviceInfo ProbeLinuxRocmFallback()
    {
        bool hasOptRocm = Directory.Exists("/opt/rocm");
        return new GpuDeviceInfo(
            Name: "Linux GPU Device",
            Architecture: "ROCm / HIP",
            DedicatedVramBytes: 0,
            IsRocmAvailable: hasOptRocm,
            IsDirectMlAvailable: false,
            RuntimeDetails: hasOptRocm ? "/opt/rocm detected" : "CPU / MIGraphX Fallback");
    }
}