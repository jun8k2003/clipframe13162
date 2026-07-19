using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipFrame.Core;

/// <summary>A named, persisted shared-region rectangle (physical screen px).</summary>
public sealed record RegionPreset(string Name, int X, int Y, int Width, int Height)
{
    [JsonIgnore]
    public Rectangle Rect => new(X, Y, Width, Height);

    public static RegionPreset FromRect(string name, Rectangle r)
        => new(name, r.X, r.Y, r.Width, r.Height);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<RegionPreset>))]
internal partial class PresetJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads/saves region presets to %APPDATA%\ClipFrame\presets.json. Names are
/// unique (case-insensitive); re-saving an existing name overwrites it.
/// </summary>
public sealed class PresetStore
{
    private readonly string _path;
    private readonly List<RegionPreset> _presets = new();

    public PresetStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipFrame");
        _path = Path.Combine(dir, "presets.json");
        Load();
    }

    public IReadOnlyList<RegionPreset> Presets => _presets;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            string json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize(json, PresetJsonContext.Default.ListRegionPreset);
            if (list != null)
            {
                _presets.Clear();
                _presets.AddRange(list);
            }
        }
        catch
        {
            // Corrupt/unreadable file — start empty rather than crashing.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(_presets, PresetJsonContext.Default.ListRegionPreset);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Best-effort persistence; ignore IO failures.
        }
    }

    /// <summary>Adds or overwrites a preset by name and persists.</summary>
    public RegionPreset Save(string name, Rectangle rect)
    {
        name = string.IsNullOrWhiteSpace(name) ? $"{rect.Width}×{rect.Height}" : name.Trim();
        _presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var preset = RegionPreset.FromRect(name, rect);
        _presets.Add(preset);
        Save();
        return preset;
    }

    public void Remove(RegionPreset preset)
    {
        _presets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        Save();
    }
}
