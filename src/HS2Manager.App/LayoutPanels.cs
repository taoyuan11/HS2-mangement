using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HS2Manager.App;

public enum Dock { Left, Top, Right, Bottom }

// Small layout panels retain the adaptive desktop layout without a WPF dependency.
public sealed class DockPanel : Panel
{
    public static readonly DependencyProperty DockProperty = DependencyProperty.RegisterAttached("Dock", typeof(Dock), typeof(DockPanel), new PropertyMetadata(Dock.Left));
    public static void SetDock(UIElement element, Dock dock) => element.SetValue(DockProperty, dock);
    public static Dock GetDock(UIElement element) => (Dock)element.GetValue(DockProperty);
    protected override Size MeasureOverride(Size available)
    {
        double usedWidth = 0, usedHeight = 0, width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(Math.Max(0, available.Width - usedWidth), Math.Max(0, available.Height - usedHeight)));
            var size = child.DesiredSize;
            if (GetDock(child) is Dock.Left or Dock.Right) { height = Math.Max(height, usedHeight + size.Height); usedWidth += size.Width; }
            else { width = Math.Max(width, usedWidth + size.Width); usedHeight += size.Height; }
        }
        return new Size(Math.Max(width, usedWidth), Math.Max(height, usedHeight));
    }
    protected override Size ArrangeOverride(Size size)
    {
        double left = 0, top = 0, right = size.Width, bottom = size.Height;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i]; var desired = child.DesiredSize;
            var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            if (i < Children.Count - 1)
                switch (GetDock(child))
                {
                    case Dock.Left: rect.Width = Math.Min(rect.Width, desired.Width); left += rect.Width; break;
                    case Dock.Right: rect.Width = Math.Min(rect.Width, desired.Width); rect.X = right - rect.Width; right -= rect.Width; break;
                    case Dock.Top: rect.Height = Math.Min(rect.Height, desired.Height); top += rect.Height; break;
                    case Dock.Bottom: rect.Height = Math.Min(rect.Height, desired.Height); rect.Y = bottom - rect.Height; bottom -= rect.Height; break;
                }
            child.Arrange(rect);
        }
        return size;
    }
}

public sealed class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, rowHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(available); var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width) { width = Math.Max(width, x); y += rowHeight; x = rowHeight = 0; }
            x += size.Width; rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(Math.Max(width, x), y + rowHeight);
    }
    protected override Size ArrangeOverride(Size available)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width) { y += rowHeight; x = rowHeight = 0; }
            child.Arrange(new Rect(x, y, size.Width, size.Height)); x += size.Width; rowHeight = Math.Max(rowHeight, size.Height);
        }
        return available;
    }
}
