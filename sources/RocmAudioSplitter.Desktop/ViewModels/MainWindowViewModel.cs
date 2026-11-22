using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RocmAudioSplitter.Core.Audio;
using RocmAudioSplitter.Core.Configuration;
using RocmAudioSplitter.Core.Inference;
using RocmAudioSplitter.Core.Streaming;
using RocmAudioSplitter.Desktop.Services;

namespace RocmAudioSplitter.Desktop.ViewModels;

public sealed record AudioSourceOption(string Label, int? ProcessId)
{
    public override string ToString() => Label;
}

public sealed record DeviceOption(string Label, string? DeviceId)
{
    public override string ToString() => Label;
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly GpuHardwareProbeService _gpuProbe = new();
    private readonly ProfileManager _profileManager = new();
    private readonly GlobalHotkeyService? _hotkeyService;
    private readonly DispatcherTimer _telemetryTimer;

    private OnnxRocmSeparationEngine? _inferenceEngine;
    private DualBusStreamEngine? _streamEngine;
    private MicrophoneCaptureService? _micCapture;
    private double _lastNonZeroSuppression = 100.0;

    [ObservableProperty] private string _gpuHeaderText = "Detecting GPU...";
    [ObservableProperty] private string _rocmStatusText = "Checking ROCm...";
    [ObservableProperty] private string _hotkeySummaryText = string.Empty;
    [ObservableProperty] private string _modelPath = string.Empty;
    [ObservableProperty] private string _engineStatusBadge = "IDLE";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;

    // Perfis JSON
    public ObservableCollection<StreamerProfile> Profiles { get; } = new();
    [ObservableProperty] private StreamerProfile? _selectedProfile;
    [ObservableProperty] private string _newProfileName = string.Empty;

    public ObservableCollection<AudioSourceOption> AudioSources { get; } = new();
    public ObservableCollection<DeviceOption> MicrophoneDevices { get; } = new();
    public ObservableCollection<DeviceOption> HeadphoneDevices { get; } = new();
    public ObservableCollection<DeviceOption> StreamOutputDevices { get; } = new();

    [ObservableProperty] private AudioSourceOption? _selectedAudioSource;
    [ObservableProperty] private DeviceOption? _selectedMicrophone;
    [ObservableProperty] private DeviceOption? _selectedHeadphone;
    [ObservableProperty] private DeviceOption? _selectedStreamOutput;

    // Controles do Mixer em Tempo Real
    [ObservableProperty] private double _vocalSuppressionPercent = 100.0;
    [ObservableProperty] private double _instrumentalGainPercent = 100.0;
    [ObservableProperty] private double _micGainDb = 0.0;
    [ObservableProperty] private double _headphoneGainPercent = 100.0;
    [ObservableProperty] private double _manualSyncOffsetMs = 0.0;
    [ObservableProperty] private bool _isMicMuted;

    // Medidores VU (0..100%)
    [ObservableProperty] private double _musicVuLevel;
    [ObservableProperty] private double _aiInstrumentalVuLevel;
    [ObservableProperty] private double _micVuLevel;
    [ObservableProperty] private double _headphoneVuLevel;
    [ObservableProperty] private double _streamVuLevel;

    // HUD de Telemetria ROCm
    [ObservableProperty] private string _activeBackendLabel = "N/A";
    [ObservableProperty] private string _lastInferenceText = "0.0 ms";
    [ObservableProperty] private string _avgInferenceText = "0.0 ms";
    [ObservableProperty] private string _totalSyncDelayText = "0 ms";
    [ObservableProperty] private string _processedChunksText = "0";
    [ObservableProperty] private string _underrunsText = "0";

    public MainWindowViewModel()
    {
        LoadHardwareInfo();
        RefreshAudioEndpoints();
        AutoLocateDefaultModel();
        LoadSavedProfiles();

        if (OperatingSystem.IsWindows())
        {
            _hotkeyService = new GlobalHotkeyService();
            HotkeySummaryText = _hotkeyService.RegistrationSummary;
            _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        }

        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _telemetryTimer.Tick += OnTelemetryTick;
        _telemetryTimer.Start();
    }

    private void LoadSavedProfiles()
    {
        ProfileStoreData store = _profileManager.Load();
        Profiles.Clear();
        foreach (var p in store.Profiles)
        {
            Profiles.Add(p);
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == store.LastSelectedProfileName)
                          ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(StreamerProfile? value)
    {
        if (value is null) return;

        VocalSuppressionPercent = value.VocalSuppressionPercent;
        InstrumentalGainPercent = value.InstrumentalGainPercent;
        MicGainDb = value.MicGainDb;
        HeadphoneGainPercent = value.HeadphoneGainPercent;
        ManualSyncOffsetMs = value.ManualSyncOffsetMs;
        IsMicMuted = value.IsMicMuted;

        if (!string.IsNullOrWhiteSpace(value.ModelPath) && File.Exists(value.ModelPath))
        {
            ModelPath = value.ModelPath;
        }

        PersistCurrentProfiles();
    }

    [RelayCommand]
    public void SaveCurrentProfile()
    {
        if (SelectedProfile is null) return;

        SelectedProfile.ModelPath = ModelPath;
        SelectedProfile.VocalSuppressionPercent = VocalSuppressionPercent;
        SelectedProfile.InstrumentalGainPercent = InstrumentalGainPercent;
        SelectedProfile.MicGainDb = MicGainDb;
        SelectedProfile.HeadphoneGainPercent = HeadphoneGainPercent;
        SelectedProfile.ManualSyncOffsetMs = ManualSyncOffsetMs;
        SelectedProfile.IsMicMuted = IsMicMuted;

        PersistCurrentProfiles();
    }

    [RelayCommand]
    public void CreateNewProfile()
    {
        string name = string.IsNullOrWhiteSpace(NewProfileName)
            ? $"Preset {Profiles.Count + 1}"
            : NewProfileName.Trim();

        var existing = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedProfile = existing;
            SaveCurrentProfile();
            NewProfileName = string.Empty;
            return;
        }

        var created = new StreamerProfile
        {
            Name = name,
            ModelPath = ModelPath,
            VocalSuppressionPercent = VocalSuppressionPercent,
            InstrumentalGainPercent = InstrumentalGainPercent,
            MicGainDb = MicGainDb,
            HeadphoneGainPercent = HeadphoneGainPercent,
            ManualSyncOffsetMs = ManualSyncOffsetMs,
            IsMicMuted = IsMicMuted
        };

        Profiles.Add(created);
        SelectedProfile = created;
        NewProfileName = string.Empty;
        PersistCurrentProfiles();
    }

    [RelayCommand]
    public void DeleteSelectedProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1) return;

        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.First();
        PersistCurrentProfiles();
    }

    private void PersistCurrentProfiles()
    {
        _profileManager.Save(new ProfileStoreData
        {
            LastSelectedProfileName = SelectedProfile?.Name,
            Profiles = Profiles.ToList()
        });
    }

    private void OnGlobalHotkeyPressed(HotkeyAction action)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            switch (action)
            {
                case HotkeyAction.ToggleVocalSuppression:
                    if (VocalSuppressionPercent > 5.0)
                    {
                        _lastNonZeroSuppression = VocalSuppressionPercent;
                        VocalSuppressionPercent = 0.0;
                    }
                    else
                    {
                        VocalSuppressionPercent = _lastNonZeroSuppression > 5.0 ? _lastNonZeroSuppression : 100.0;
                    }
                    break;

                case HotkeyAction.ToggleMicMute:
                    IsMicMuted = !IsMicMuted;
                    break;

                case HotkeyAction.ToggleLiveEngine:
                    await ToggleEngineAsync();
                    break;

                case HotkeyAction.LoadProfile1 when Profiles.Count >= 1:
                    SelectedProfile = Profiles[0];
                    break;

                case HotkeyAction.LoadProfile2 when Profiles.Count >= 2:
                    SelectedProfile = Profiles[1];
                    break;

                case HotkeyAction.LoadProfile3 when Profiles.Count >= 3:
                    SelectedProfile = Profiles[2];
                    break;
            }
        });
    }

    private void LoadHardwareInfo()
    {
        GpuDeviceInfo info = _gpuProbe.ProbeSystemGpu();
        long vramMb = info.DedicatedVramBytes / (1024 * 1024);
        GpuHeaderText = $"{info.Name} ({info.Architecture}) • {vramMb} MB VRAM";
        RocmStatusText = info.IsRocmAvailable
            ? $"ROCm READY ({info.RuntimeDetails})"
            : $"Fallback Mode ({info.RuntimeDetails})";
    }

    private void AutoLocateDefaultModel()
    {
        string[] searchRoots =
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        foreach (string root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;
            string? found = Directory.EnumerateFiles(root, "*KARA*.onnx", SearchOption.TopDirectoryOnly).FirstOrDefault()
                         ?? Directory.EnumerateFiles(root, "*.onnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(found))
            {
                ModelPath = found;
                break;
            }
        }
    }

    [RelayCommand]
    public void RefreshAudioEndpoints()
    {
        int? prevPid = SelectedAudioSource?.ProcessId;
        string? prevMic = SelectedMicrophone?.DeviceId;
        string? prevHp = SelectedHeadphone?.DeviceId;
        string? prevStream = SelectedStreamOutput?.DeviceId;

        AudioSources.Clear();
        AudioSources.Add(new AudioSourceOption("System Master Output (All Apps)", null));
        foreach (var proc in AudioProcessEnumerator.GetActiveAudioProcesses())
        {
            AudioSources.Add(new AudioSourceOption($"[App] {proc.DisplayLabel}", proc.ProcessId));
        }
        SelectedAudioSource = AudioSources.FirstOrDefault(s => s.ProcessId == prevPid) ?? AudioSources.First();

        MicrophoneDevices.Clear();
        foreach (var mic in WasapiDeviceEnumerator.GetCaptureDevices())
        {
            MicrophoneDevices.Add(new DeviceOption(mic.FriendlyName + (mic.IsDefault ? " (Default)" : ""), mic.Id));
        }
        SelectedMicrophone = MicrophoneDevices.FirstOrDefault(d => d.DeviceId == prevMic) ?? MicrophoneDevices.FirstOrDefault();

        HeadphoneDevices.Clear();
        StreamOutputDevices.Clear();
        var playbackList = WasapiDeviceEnumerator.GetPlaybackDevices();
        foreach (var pb in playbackList)
        {
            string tag = pb.IsVirtualCable ? " [Virtual Cable]" : (pb.IsDefault ? " (Default)" : "");
            var opt = new DeviceOption(pb.FriendlyName + tag, pb.Id);
            HeadphoneDevices.Add(opt);
            StreamOutputDevices.Add(opt);
        }

        SelectedHeadphone = HeadphoneDevices.FirstOrDefault(d => d.DeviceId == prevHp)
                            ?? HeadphoneDevices.FirstOrDefault(d => !d.Label.Contains("[Virtual Cable]"))
                            ?? HeadphoneDevices.FirstOrDefault();

        SelectedStreamOutput = StreamOutputDevices.FirstOrDefault(d => d.DeviceId == prevStream)
                               ?? StreamOutputDevices.FirstOrDefault(d => d.Label.Contains("[Virtual Cable]"))
                               ?? StreamOutputDevices.LastOrDefault();
    }

    [RelayCommand]
    public async Task ToggleEngineAsync()
    {
        if (IsBusy) return;

        if (IsRunning)
        {
            await StopEngineInternalAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelPath) || !File.Exists(ModelPath))
        {
            EngineStatusBadge = "MODEL NOT FOUND";
            return;
        }

        IsBusy = true;
        EngineStatusBadge = "INITIALIZING GPU...";

        try
        {
            _inferenceEngine = new OnnxRocmSeparationEngine(_gpuProbe);
            await _inferenceEngine.InitializeAsync(ModelPath);
            ActiveBackendLabel = _inferenceEngine.ActiveBackend.ToString();

            int? targetPid = SelectedAudioSource?.ProcessId;
            var musicCapture = new WasapiAudioCaptureService(
                playbackDeviceId: null,
                targetProcessId: targetPid);

            _micCapture = new MicrophoneCaptureService(SelectedMicrophone?.DeviceId);
            _micCapture.SetGainDb((float)MicGainDb);
            _micCapture.SetMuted(IsMicMuted);

            _streamEngine = new DualBusStreamEngine(
                _inferenceEngine,
                musicCapture,
                _micCapture,
                headphoneDeviceId: SelectedHeadphone?.DeviceId,
                virtualCableDeviceId: SelectedStreamOutput?.DeviceId);

            ApplyLiveMixerParameters();

            if (targetPid is null)
            {
                _streamEngine.SetHeadphoneGain(0f);
                EngineStatusBadge = $"LIVE ({_inferenceEngine.ActiveBackend}) • HP MUTED (Avoid Loopback Echo)";
            }
            else
            {
                EngineStatusBadge = $"LIVE ({_inferenceEngine.ActiveBackend}) • APP ISOLATED";
            }

            _streamEngine.Start();
            IsRunning = true;
        }
        catch (Exception ex)
        {
            EngineStatusBadge = $"ERROR: {ex.Message}";
            await StopEngineInternalAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopEngineInternalAsync()
    {
        IsBusy = true;
        try
        {
            if (_streamEngine is not null)
            {
                await _streamEngine.DisposeAsync();
                _streamEngine = null;
            }
            if (_inferenceEngine is not null)
            {
                await _inferenceEngine.DisposeAsync();
                _inferenceEngine = null;
            }
            _micCapture = null;
        }
        finally
        {
            IsRunning = false;
            IsBusy = false;
            EngineStatusBadge = "STOPPED";
            MusicVuLevel = AiInstrumentalVuLevel = MicVuLevel = HeadphoneVuLevel = StreamVuLevel = 0;
        }
    }

    partial void OnVocalSuppressionPercentChanged(double value) => ApplyLiveMixerParameters();
    partial void OnInstrumentalGainPercentChanged(double value) => ApplyLiveMixerParameters();
    partial void OnMicGainDbChanged(double value) => ApplyLiveMixerParameters();
    partial void OnHeadphoneGainPercentChanged(double value) => ApplyLiveMixerParameters();
    partial void OnManualSyncOffsetMsChanged(double value) => ApplyLiveMixerParameters();
    partial void OnIsMicMutedChanged(bool value) => ApplyLiveMixerParameters();

    private void ApplyLiveMixerParameters()
    {
        _streamEngine?.SetVocalSuppressionMix((float)(VocalSuppressionPercent / 100.0));
        _streamEngine?.SetInstrumentalGain((float)(InstrumentalGainPercent / 100.0));
        if (SelectedAudioSource?.ProcessId is not null)
        {
            _streamEngine?.SetHeadphoneGain((float)(HeadphoneGainPercent / 100.0));
        }
        _streamEngine?.SetManualSyncOffsetMs(ManualSyncOffsetMs);
        _micCapture?.SetGainDb((float)MicGainDb);
        _micCapture?.SetMuted(IsMicMuted);
    }

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        if (_streamEngine is null || !IsRunning) return;

        LiveTelemetrySnapshot snap = _streamEngine.GetTelemetry();
        LastInferenceText = $"{snap.LastInferenceMs:F1} ms";
        AvgInferenceText = $"{snap.RollingAverageInferenceMs:F1} ms";
        TotalSyncDelayText = $"{snap.TotalPipelineLatencyMs:F0} ms";
        ProcessedChunksText = snap.ProcessedChunks.ToString("N0");
        UnderrunsText = snap.BufferUnderruns.ToString("N0");

        MusicVuLevel = SmoothVu(MusicVuLevel, snap.Levels.MusicInputPeak * 100.0);
        AiInstrumentalVuLevel = SmoothVu(AiInstrumentalVuLevel, snap.Levels.AiInstrumentalPeak * 100.0);
        MicVuLevel = SmoothVu(MicVuLevel, snap.Levels.MicInputPeak * 100.0);
        HeadphoneVuLevel = SmoothVu(HeadphoneVuLevel, snap.Levels.HeadphoneOutPeak * 100.0);
        StreamVuLevel = SmoothVu(StreamVuLevel, snap.Levels.StreamOutPeak * 100.0);
    }

    private static double SmoothVu(double current, double target)
    {
        return target >= current ? target : Math.Max(0, current * 0.82);
    }

    public async Task ShutdownAsync()
    {
        _telemetryTimer.Stop();
        if (OperatingSystem.IsWindows() && _hotkeyService is not null)
        {
            _hotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;
            _hotkeyService.Dispose();
        }
        await StopEngineInternalAsync();
    }
}