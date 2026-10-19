using System.ComponentModel;
using System.Windows.Media;

namespace YorteImpact.Models;

/// <summary>One direction preview for the gradient transparency card.</summary>
public sealed class TransparencyPresetItem : INotifyPropertyChanged
{
    private bool _isActive;

    public TransparencyPresetItem(string name, string toolTip,
        OpacityFadeDirection direction, Brush previewBrush)
    {
        Name = name;
        ToolTip = toolTip;
        Direction = direction;
        PreviewBrush = previewBrush;
    }

    public string Name { get; }
    public string ToolTip { get; }
    public OpacityFadeDirection Direction { get; }
    public Brush PreviewBrush { get; }

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
