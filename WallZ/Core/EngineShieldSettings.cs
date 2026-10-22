using System.IO;
using System.Text.Json;

namespace WallZ.Core;

public sealed class EngineShieldSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WALL Z",
        "shield-settings.json");

    private static readonly object Lock = new();
    private static EngineShieldSettings? _instance;

    public bool AutoPauseOnFullscreen { get; set; } = true;
    public bool BatterySaverMode { get; set; } = true;
    public bool SleepDisplayStandby { get; set; } = true;

    public static EngineShieldSettings Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (Lock)
                {
                    _instance ??= Load();
                }
            }
            return _instance;
        }
    }

    public static EngineShieldSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<EngineShieldSettings>(json);
                if (settings != null)
                    return settings;
            }
        }
        catch { }

        return new EngineShieldSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFile);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            lock (Lock)
            {
                File.WriteAllText(SettingsFile, json);
            }
        }
        catch { }
    }
}
