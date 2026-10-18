using System.Windows;

namespace YorteImpact.Models;

/// <summary>Simple slice/export rectangle in pasteboard coordinates.</summary>
public sealed class SliceRegion
{
    public Rect Rect { get; set; }
    public string Name { get; set; } = "slice";

    public bool IsEmpty => Rect.Width < 2 || Rect.Height < 2;
}
