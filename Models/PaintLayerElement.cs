using System.Windows.Media;

namespace YorteImpact.Models;

/// <summary>
/// Raster paint layer that sits directly above the canvas paper and below every
/// regular object. It is not selectable or hit-testable: the paintbrush writes
/// into its bitmap, and the layer participates in export/undo like any object.
/// </summary>
public class PaintLayerElement : DesignElement
{
    /// <summary>Reserved stacking slot: always below regular elements.</summary>
    public const int BackgroundZ = -10000;

    private ImageSource? _source;

    public ImageSource? Source
    {
        get => _source;
        set => Set(ref _source, value);
    }

    public override bool IsBackgroundLayer => true;

    public override DesignElement Clone()
    {
        var c = new PaintLayerElement
        {
            X = X, Y = Y,
            Width = Width, Height = Height,
            ZIndex = BackgroundZ,
            Source = Source,
        };
        return c;
    }
}
