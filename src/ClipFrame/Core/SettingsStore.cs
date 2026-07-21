using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipFrame.Core;

/// <summary>Persisted user preferences (not tied to a specific region).</summary>
public sealed class AppSettings
{
    /// <summary>Show the "mirror overlaps the shared region" warning (spec §5).</summary>
    public bool ShowOverlapWarning { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads/saves user preferences to %APPDATA%\ClipFrame\settings.json.
/// Best-effort: a corrupt or unwritable file falls back to defaults.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipFrame");
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                   ?? new AppSettings();
        }
        catch
        {
            // Corrupt/unreadable file — start with defaults rather than crashing.
            return new AppSettings();
        }
    }

    /// <summary>Persists the current settings. Best-effort; IO failures are ignored.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Best-effort persistence; ignore IO failures.
        }
    }
}
