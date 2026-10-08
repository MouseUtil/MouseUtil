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

    /// <summary>
    /// TEMPORARY — remove once 2.2.0 users have migrated. "PauseOnMovement" was the master on/off
    /// switch pre-redesign; AppConfig no longer declares it, so a normal deserialize silently drops it.
    /// If config.json still has it set to false, both per-mode flags must be forced off too, or an
    /// update from 2.2.0 would silently re-enable pausing. Self-cleans: forcing the flags rewrites
    /// config.json without the obsolete field, so this is a no-op on every launch after the first.
    /// </summary>
    public static void MigrateLegacyPauseOnMovementOff()
    {
        lock (FileLock)
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(ConfigPath);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("PauseOnMovement", out var legacyValue)
                    && legacyValue.ValueKind == JsonValueKind.False)
                {
                    var config = LoadUnlocked();
                    config.PauseOnMovementForAutoClick = false;
                    config.PauseOnMovementForJiggle = false;
                    var updatedJson = JsonSerializer.Serialize(config, SerializerOptions);
                    File.WriteAllText(ConfigPath, updatedJson);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Fall through silently - same tolerance as LoadUnlocked.
            }
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
