using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;

namespace YorteImpact.Models;

/// <summary>
/// A container that groups child elements into a single selectable unit.
/// Children keep their own type (text, shape, image) and render unchanged,
/// but are moved, selected and reordered together with the group.
/// Child coordinates are relative to the group's top-left corner.
/// </summary>
public class GroupElement : DesignElement
{
    public ObservableCollection<DesignElement> Children { get; } = new();

    private double _groupWidth;
    private double _groupHeight;

    public GroupElement()
    {
        Children.CollectionChanged += OnChildrenChanged;
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (DesignElement child in e.OldItems)
                child.PropertyChanged -= OnChildPropertyChanged;
        if (e.NewItems != null)
            foreach (DesignElement child in e.NewItems)
                child.PropertyChanged += OnChildPropertyChanged;
        Raise(nameof(Bounds));
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Bounds))
            Raise(nameof(Bounds));
    }

    public override double Width
    {
        get => _groupWidth;
        set => SetScaled(ref _groupWidth, value, nameof(Width));
    }

    public override double Height
    {
        get => _groupHeight;
        set => SetScaled(ref _groupHeight, value, nameof(Height));
    }

    private void SetScaled(ref double field, double value, string propertyName)
    {
        if (Set(ref field, Math.Max(1, value), propertyName))
        {
            ScaleChildren(propertyName == nameof(Width));
            Raise(nameof(Bounds));
        }
    }

    private double _origW;
    private double _origH;

    private void ScaleChildren(bool scaleFont)
    {
        if (_origW <= 0 || _origH <= 0)
        {
            _origW = _groupWidth;
            _origH = _groupHeight;
            return;
        }
        double sx = _groupWidth / _origW;
        double sy = _groupHeight / _origH;
        foreach (var child in Children)
        {
            if (child is TextElement text)
            {
                if (scaleFont)
                {
                    bool anchoring = text.Anchoring;
                    text.Anchoring = false;
                    text.FontSize = Math.Clamp(text.FontSize * sx, 6, 300);
                    text.Anchoring = anchoring;
                }
                text.X *= sx;
                text.Y *= sy;
                continue;
            }
            child.X *= sx;
            child.Y *= sy;
            child.Width *= sx;
            child.Height *= sy;
        }
        _origW = _groupWidth;
        _origH = _groupHeight;
    }

    public override Rect Bounds
    {
        get
        {
            if (Children.Count == 0) return new Rect(X, Y, Width, Height);
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var child in Children)
            {
                minX = Math.Min(minX, child.X);
                minY = Math.Min(minY, child.Y);
                maxX = Math.Max(maxX, child.X + child.Width);
                maxY = Math.Max(maxY, child.Y + child.Height);
            }
            return new Rect(X + minX, Y + minY, maxX - minX, maxY - minY);
        }
    }

    public void CaptureOriginalSize()
    {
        _origW = _groupWidth;
        _origH = _groupHeight;
    }

    public override GroupElement Clone()
    {
        var clone = new GroupElement
        {
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            RotationAngle = RotationAngle,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
        };
        CopyStyleTo(clone);
        foreach (var child in Children)
            clone.Children.Add(child.Clone());
        clone.CaptureOriginalSize();
        return clone;
    }
}
