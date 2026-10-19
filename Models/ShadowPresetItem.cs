using System.ComponentModel;
using System.Windows.Media.Effects;

namespace YorteImpact.Models;

/// <summary>One selectable shadow preset with a small preview effect.</summary>
public sealed class ShadowPresetItem : INotifyPropertyChanged
{
    private bool _isActive;

    public ShadowPresetItem(string name, ShadowStyle style, DropShadowEffect? previewEffect)
    {
        Name = name;
        Style = style;
        PreviewEffect = previewEffect;
    }

    public string Name { get; }
    public ShadowStyle Style { get; }
    public DropShadowEffect? PreviewEffect { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
