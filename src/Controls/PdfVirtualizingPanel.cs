using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Generators;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Controls;

public sealed class PdfVirtualizingPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> BufferFactorProperty =
        AvaloniaProperty.Register<PdfVirtualizingPanel, double>(
            nameof(BufferFactor),
            1.0,
            validate: value => value is >= 0 and <= 2);

    public static readonly StyledProperty<int> LayoutVersionProperty =
        AvaloniaProperty.Register<PdfVirtualizingPanel, int>(nameof(LayoutVersion));

    private readonly Dictionary<int, Control> _realized = new();
    private readonly Stack<Control> _recyclePool = new();
    private double[] _offsets = [];
    private double _maxItemWidth;
    private int _itemsCount;
    private int _measuredLayoutVersion = -1;
    private Rect _viewport;
    private (int Index, double Ratio)? _pendingAnchor;
    private int _scrollRestoreVersion;

    public PdfVirtualizingPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    static PdfVirtualizingPanel()
    {
        BufferFactorProperty.Changed.AddClassHandler<PdfVirtualizingPanel>(
            (panel, _) => panel.InvalidateMeasure());
        LayoutVersionProperty.Changed.AddClassHandler<PdfVirtualizingPanel>(
            (panel, _) => panel.OnLayoutVersionChanged());
    }

    public double BufferFactor
    {
        get => GetValue(BufferFactorProperty);
        set => SetValue(BufferFactorProperty, value);
    }

    public int LayoutVersion
    {
        get => GetValue(LayoutVersionProperty);
        set => SetValue(LayoutVersionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Items.Count == 0)
        {
            RecycleAll();
            _offsets = [];
            _itemsCount = 0;
            return default;
        }

        bool layoutChanged = EnsureOffsets();
        if (layoutChanged && _pendingAnchor is { } anchor)
        {
            double itemHeight = _offsets[anchor.Index + 1] - _offsets[anchor.Index];
            double targetY = _offsets[anchor.Index] + itemHeight * anchor.Ratio;
            _pendingAnchor = null;
            RestoreScrollPosition(targetY);
        }

        if (_viewport.Height <= 0 && double.IsFinite(availableSize.Height))
        {
            _viewport = new Rect(0, 0, availableSize.Width, availableSize.Height);
        }

        GetRealizationRange(out int firstIndex, out int lastIndex);
        foreach (int index in _realized.Keys.Where(index => index < firstIndex || index > lastIndex).ToArray())
        {
            Recycle(index);
        }

        for (int index = firstIndex; index <= lastIndex; index++)
        {
            Control container = GetOrCreate(index);
            var page = (PdfPageViewModel)Items[index]!;
            container.Measure(new Size(page.ItemWidth, page.ItemHeight));
        }

        return new Size(_maxItemWidth, _offsets[^1]);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, container) in _realized)
        {
            var page = (PdfPageViewModel)Items[index]!;
            double x = Math.Max(0, (finalSize.Width - page.ItemWidth) / 2);
            container.Arrange(new Rect(x, _offsets[index], page.ItemWidth, page.ItemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items,
        NotifyCollectionChangedEventArgs e)
    {
        RecycleAll();
        _offsets = [];
        _itemsCount = 0;
        _pendingAnchor = null;
        _scrollRestoreVersion++;
        InvalidateMeasure();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RecycleAll();
        _viewport = default;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Control? ContainerFromIndex(int index) =>
        _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
        {
            if (ReferenceEquals(realized, container)) return index;
        }

        return -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers() => _realized.Values;

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        EnsureOffsets();

        if (this.FindAncestorOfType<ScrollViewer>() is { } scrollViewer)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, _offsets[index]);
            InvalidateMeasure();
        }

        return ContainerFromIndex(index);
    }

    protected override IInputElement? GetControl(
        NavigationDirection direction,
        IInputElement? from,
        bool wrap)
    {
        int count = Items.Count;
        if (count == 0) return null;

        int fromIndex = from as Control is { } control ? IndexFromContainer(control) : -1;
        int toIndex = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Down => fromIndex + 1,
            NavigationDirection.Previous or NavigationDirection.Up => fromIndex - 1,
            _ => fromIndex,
        };

        if (wrap && count > 0)
        {
            if (toIndex < 0) toIndex = count - 1;
            else if (toIndex >= count) toIndex = 0;
        }

        return toIndex >= 0 && toIndex < count ? ScrollIntoView(toIndex) : null;
    }

    private bool EnsureOffsets()
    {
        if (_offsets.Length == Items.Count + 1 &&
            _itemsCount == Items.Count &&
            _measuredLayoutVersion == LayoutVersion)
        {
            return false;
        }

        _offsets = new double[Items.Count + 1];
        _maxItemWidth = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            var page = (PdfPageViewModel)Items[i]!;
            _maxItemWidth = Math.Max(_maxItemWidth, page.ItemWidth);
            _offsets[i + 1] = _offsets[i] + page.ItemHeight;
        }

        _itemsCount = Items.Count;
        _measuredLayoutVersion = LayoutVersion;
        return true;
    }

    private void GetRealizationRange(out int firstIndex, out int lastIndex)
    {
        if (Items.Count == 0 || _viewport.Height <= 0)
        {
            firstIndex = 1;
            lastIndex = 0;
            return;
        }

        double buffer = _viewport.Height * BufferFactor;
        double start = Math.Max(0, _viewport.Top - buffer);
        double end = Math.Min(_offsets[^1], _viewport.Bottom + buffer);
        firstIndex = FindPageIndex(start);
        lastIndex = firstIndex;
        while (lastIndex + 1 < Items.Count && _offsets[lastIndex + 1] < end)
        {
            lastIndex++;
        }
    }

    private int FindPageIndex(double y)
    {
        int low = 0;
        int high = Items.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_offsets[middle + 1] <= y) low = middle + 1;
            else high = middle - 1;
        }

        return Math.Clamp(low, 0, Items.Count - 1);
    }

    private Control GetOrCreate(int index)
    {
        if (_realized.TryGetValue(index, out Control? realized)) return realized;

        object? item = Items[index];
        ItemContainerGenerator generator = ItemContainerGenerator!;
        if (!_recyclePool.TryPop(out Control? container))
        {
            container = generator.CreateContainer(item, index, typeof(PdfPageViewModel));
        }

        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        _realized.Add(index, container);
        return container;
    }

    private void Recycle(int index)
    {
        if (!_realized.Remove(index, out Control? container)) return;
        ItemContainerGenerator!.ClearItemContainer(container);
        RemoveInternalChild(container);
        _recyclePool.Push(container);
    }

    private void RecycleAll()
    {
        foreach (int index in _realized.Keys.ToArray()) Recycle(index);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        Rect previous = _viewport;
        _viewport = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        if (Items.Count == 0 || _offsets.Length != Items.Count + 1)
        {
            InvalidateMeasure();
            return;
        }

        if (previous.Size != _viewport.Size || !RealizedRangeCoversViewport())
        {
            InvalidateMeasure();
        }
    }

    private bool RealizedRangeCoversViewport()
    {
        if (_realized.Count == 0 || _viewport.Height <= 0) return false;
        double buffer = _viewport.Height * BufferFactor;
        double start = Math.Max(0, _viewport.Top - buffer);
        double end = Math.Min(_offsets[^1], _viewport.Bottom + buffer);
        int first = _realized.Keys.Min();
        int last = _realized.Keys.Max();
        return _offsets[first] <= start && _offsets[last + 1] >= end;
    }

    private void OnLayoutVersionChanged()
    {
        if (_offsets.Length == Items.Count + 1 && Items.Count > 0 && _viewport.Height > 0)
        {
            int index = FindPageIndex(_viewport.Top);
            double itemHeight = _offsets[index + 1] - _offsets[index];
            double ratio = itemHeight > 0
                ? Math.Clamp((_viewport.Top - _offsets[index]) / itemHeight, 0, 1)
                : 0;
            _pendingAnchor = (index, ratio);
        }

        InvalidateMeasure();
    }

    private void RestoreScrollPosition(double targetY)
    {
        int restoreVersion = ++_scrollRestoreVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (restoreVersion != _scrollRestoreVersion) return;
            if (this.FindAncestorOfType<ScrollViewer>() is { } scrollViewer)
            {
                double maxY = Math.Max(0, _offsets[^1] - _viewport.Height);
                scrollViewer.Offset = new Vector(
                    scrollViewer.Offset.X,
                    Math.Clamp(targetY, 0, maxY));
            }
        }, DispatcherPriority.Loaded);
    }
}
