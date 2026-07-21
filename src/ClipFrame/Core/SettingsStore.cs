using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipFrame.Core;

/// <summary>A rectangle in physical screen pixels, for JSON persistence.</summary>
public sealed record RectInfo(int X, int Y, int Width, int Height);

/// <summary>Persisted user preferences (not tied to a specific region).</summary>
public sealed class AppSettings
{
    /// <summary>Show the "mirror overlaps the shared region" warning (spec §5).</summary>
    public bool ShowOverlapWarning { get; set; } = true;

    /// <summary>
    /// Monitor layout (each monitor's physical-px rect) captured at last exit.
    /// On the next launch, <see cref="LastRegion"/>/<see cref="LastMirrorWindow"/>
    /// are only restored if the current layout matches this one exactly (same
    /// screen count and same geometry) — otherwise a changed monitor setup
    /// could place windows off-screen.
    /// </summary>
    public List<RectInfo>? LastMonitorLayout { get; set; }

    /// <summary>Shared region rectangle (physical px) at last exit.</summary>
    public RectInfo? LastRegion { get; set; }

    /// <summary>Mirror window rectangle (physical px) at last exit.</summary>
    public RectInfo? LastMirrorWindow { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<RectInfo>))]
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
