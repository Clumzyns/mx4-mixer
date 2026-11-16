using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mixer.Config;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);

    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static MixerConfig Load()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!File.Exists(FilePath))
            {
                var created = MixerConfig.CreateDefault();
                Save(created);
                return created;
            }

            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<MixerConfig>(json, Json) ?? MixerConfig.CreateDefault();
            config.EnsureShape();
            return config;
        }
        catch (Exception ex)
        {
            Services.Log.Error("Config load failed. Starting from defaults.", ex);
            TryQuarantine();
            var fresh = MixerConfig.CreateDefault();
            try { Save(fresh); } catch { /* the next edit will try again */ }
            return fresh;
        }
    }

    public static void Save(MixerConfig config)
    {
        config.EnsureShape();
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(config, Json);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    private static void TryQuarantine()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dest = Path.Combine(DirectoryPath, "config.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.Move(FilePath, dest, overwrite: true);
        }
        catch
        {
            // Keep going with defaults even if the broken file cannot be moved.
        }
    }
}
