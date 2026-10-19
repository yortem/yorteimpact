using System.Windows;
using System.Windows.Media;

namespace YorteImpact.Models;

/// <summary>Slice selection in pasteboard coordinates.</summary>
public sealed class SliceRegion
{
    public Rect Rect { get; set; }
    public string Name { get; set; } = "slice";
    public PathGeometry? SelectionGeometry { get; set; }
    public bool IsBezier => SelectionGeometry != null;

    public bool IsEmpty => Rect.Width < 2 || Rect.Height < 2;
}
