using System.Drawing;

namespace ClipFrame.Core;

/// <summary>
/// Single source of truth for the shared region, expressed in <b>physical
/// screen pixels</b>. The overlay window owns editing; the mirror and capture
/// engine observe.
/// </summary>
public sealed class RegionManager
{
    private Rectangle _region;

    public RegionManager(Rectangle initial)
    {
        _region = initial;
    }

    /// <summary>Current shared region in physical screen pixels.</summary>
    public Rectangle CurrentRegion => _region;

    /// <summary>Raised repeatedly while the user is dragging/resizing (live geometry).</summary>
    public event Action<Rectangle>? RegionLive;

    /// <summary>Raised once when a drag/resize begins (freeze the mirror).</summary>
    public event Action? RegionChanging;

    /// <summary>Raised once when a drag/resize is committed (mouse up).</summary>
    public event Action<Rectangle>? RegionCommitted;

    /// <summary>Called by the overlay during an interactive change.</summary>
    public void ReportLive(Rectangle region)
    {
        _region = region;
        RegionLive?.Invoke(region);
    }

    public void BeginChange() => RegionChanging?.Invoke();

    public void Commit(Rectangle region)
    {
        _region = region;
        RegionCommitted?.Invoke(region);
    }
}
