using System.Text.Json;
using MouseUtil.Models;

namespace MouseUtil.Services;

/// <summary>
/// Persists app settings to %APPDATA%\MouseUtil\config.json.
/// Every write reloads the file first and mutates a single field, so concurrent
/// setting changes never clobber each other.
/// </summary>
internal static class ConfigService
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MouseUtil");

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "config.json");

    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        lock (FileLock)
        {
            return LoadUnlocked();
        }
    }

    public static void Update(Action<AppConfig> mutate)
    {
        lock (FileLock)
        {
            var config = LoadUnlocked();
            mutate(config);
            var json = JsonSerializer.Serialize(config, SerializerOptions);
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, json);
        }
    }

    private static AppConfig LoadUnlocked()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    return config;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults if the file is missing, unreadable, or corrupt.
        }

        return new AppConfig();
    }
}
