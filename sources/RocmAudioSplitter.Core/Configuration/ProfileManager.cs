using System.Text.Json;
using System.Text.Json.Serialization;

namespace RocmAudioSplitter.Core.Configuration;

public sealed class StreamerProfile
{
    public string Name { get; set; } = "Default";
    public string? ModelPath { get; set; }
    public double VocalSuppressionPercent { get; set; } = 100.0;
    public double InstrumentalGainPercent { get; set; } = 100.0;
    public double MicGainDb { get; set; } = 0.0;
    public double HeadphoneGainPercent { get; set; } = 100.0;
    public double ManualSyncOffsetMs { get; set; } = 0.0;
    public bool IsMicMuted { get; set; } = false;

    public override string ToString() => Name;
}

public sealed class ProfileStoreData
{
    public string? LastSelectedProfileName { get; set; }
    public List<StreamerProfile> Profiles { get; set; } = new();
}

public sealed class ProfileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string StorageFilePath { get; }

    public ProfileManager(string? customDirectory = null)
    {
        string baseDir = customDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RocmAudioSplitter");

        Directory.CreateDirectory(baseDir);
        StorageFilePath = Path.Combine(baseDir, "profiles.json");
    }

    public ProfileStoreData Load()
    {
        if (!File.Exists(StorageFilePath))
        {
            var initial = CreateDefaultStore();
            Save(initial);
            return initial;
        }

        try
        {
            string json = File.ReadAllText(StorageFilePath);
            var data = JsonSerializer.Deserialize<ProfileStoreData>(json, JsonOptions);
            if (data is null || data.Profiles.Count == 0)
            {
                var fallback = CreateDefaultStore();
                Save(fallback);
                return fallback;
            }
            return data;
        }
        catch
        {
            return CreateDefaultStore();
        }
    }

    public void Save(ProfileStoreData data)
    {
        string json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(StorageFilePath, json);
    }

    private static ProfileStoreData CreateDefaultStore()
    {
        return new ProfileStoreData
        {
            LastSelectedProfileName = "Karaoke / Live Singing",
            Profiles = new List<StreamerProfile>
            {
                new()
                {
                    Name = "Karaoke / Live Singing",
                    VocalSuppressionPercent = 100.0,
                    InstrumentalGainPercent = 100.0,
                    MicGainDb = 2.0,
                    HeadphoneGainPercent = 100.0,
                    ManualSyncOffsetMs = 0.0,
                    IsMicMuted = false
                },
                new()
                {
                    Name = "Soft Backing Blend (85%)",
                    VocalSuppressionPercent = 85.0,
                    InstrumentalGainPercent = 105.0,
                    MicGainDb = 1.5,
                    HeadphoneGainPercent = 100.0,
                    ManualSyncOffsetMs = 0.0,
                    IsMicMuted = false
                },
                new()
                {
                    Name = "Normal Gaming / Talk (Bypass)",
                    VocalSuppressionPercent = 0.0,
                    InstrumentalGainPercent = 70.0,
                    MicGainDb = 0.0,
                    HeadphoneGainPercent = 100.0,
                    ManualSyncOffsetMs = 0.0,
                    IsMicMuted = false
                }
            }
        };
    }
}