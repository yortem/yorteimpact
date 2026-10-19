using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace YorteImpact.Models;

/// <summary>A built-in photo look shown as a thumb in the Photo tab.</summary>
public sealed class PhotoPreset : INotifyPropertyChanged
{
    private BitmapSource? _preview;

    public PhotoPreset(string name, PhotoAdjustments adjustments, bool isAuto = false)
    {
        Name = name;
        Adjustments = adjustments;
        IsAuto = isAuto;
    }

    public string Name { get; }
    public PhotoAdjustments Adjustments { get; }
    public bool IsAuto { get; }

    public BitmapSource? Preview
    {
        get => _preview;
        set
        {
            if (ReferenceEquals(_preview, value)) return;
            _preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
