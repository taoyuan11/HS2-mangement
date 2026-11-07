using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HS2Manager.App;

/// <summary>Pixel-scrolling, recycling card grid. Only visible rows and one buffer row have WPF containers.</summary>
public sealed class VirtualizingCardPanel : VirtualizingPanel, IScrollInfo
{
    public double CellWidth { get; set; } = 190;
    public double CellHeight { get; set; } = 302;
    private Size extent, viewport;
    private double offset;
    private int columns = 1;
    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return new Size();
        double width = double.IsInfinity(available.Width) ? Math.Max(ScrollOwner?.ActualWidth ?? 600, CellWidth) : available.Width;
        double height = double.IsInfinity(available.Height) ? Math.Max(ScrollOwner?.ActualHeight ?? 600, CellHeight) : available.Height;
        viewport = new Size(Math.Max(0, width), Math.Max(0, height));
        columns = Math.Max(1, (int)(width / CellWidth));
        extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)columns) * CellHeight);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
        int first = Math.Max(0, ((int)(offset / CellHeight) - 1) * columns);
        int last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((offset + height) / CellHeight) + 1) * columns - 1);
        // Accessing InternalChildren connects the ItemsControl's generator on first measure.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        var position = generator.GeneratorPositionFromIndex(first);
        int childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            for (int index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out bool created);
                if (created)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(CellWidth, CellHeight));
            }
        }
        for (int child = InternalChildren.Count - 1; child >= 0; child--)
        {
            var pos = new GeneratorPosition(child, 0); int index = generator.IndexFromGeneratorPosition(pos);
            if (index < first || index > last)
            {
                if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(pos, 1); else generator.Remove(pos, 1);
                RemoveInternalChildRange(child, 1);
            }
        }
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double inset = Math.Max(0, (finalSize.Width - columns * CellWidth) / 2);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(inset + (index % columns) * CellWidth, index / columns * CellHeight - offset, CellWidth, CellHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
        {
            int index = args.Position.Index;
            if (index >= 0 && index < InternalChildren.Count && args.ItemUICount > 0) RemoveInternalChildRange(index, Math.Min(args.ItemUICount, InternalChildren.Count - index));
        }
        InvalidateMeasure(); base.OnItemsChanged(sender, args);
    }
    protected override void BringIndexIntoView(int index) { SetVerticalOffset(index / columns * CellHeight); }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return rectangle;
        var container = ItemsControl.ContainerFromElement(owner, visual);
        int index = container == null ? -1 : owner.ItemContainerGenerator.IndexFromContainer(container);
        if (index >= 0)
        {
            double top = index / columns * CellHeight;
            if (top < offset) SetVerticalOffset(top); else if (top + CellHeight > offset + viewport.Height) SetVerticalOffset(top + CellHeight - viewport.Height);
        }
        return rectangle;
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentHeight => extent.Height;
    public double ExtentWidth => extent.Width;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public double ViewportHeight => viewport.Height;
    public double ViewportWidth => viewport.Width;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetVerticalOffset(double value)
    {
        value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, Math.Max(0, extent.Height - viewport.Height));
        if (Math.Abs(value - offset) < .1) return; offset = value; ScrollOwner?.InvalidateScrollInfo(); InvalidateMeasure();
    }
    public void SetHorizontalOffset(double value) { }
    public void LineUp() => SetVerticalOffset(offset - 40);
    public void LineDown() => SetVerticalOffset(offset + 40);
    public void MouseWheelUp() => SetVerticalOffset(offset - 120);
    public void MouseWheelDown() => SetVerticalOffset(offset + 120);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void LineLeft() { } public void LineRight() { } public void MouseWheelLeft() { } public void MouseWheelRight() { } public void PageLeft() { } public void PageRight() { }
}
