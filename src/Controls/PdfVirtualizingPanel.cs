using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Generators;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PdfVirtualizingPanel, double>(nameof(Zoom), 1.0);

    /// <summary>
    /// Space around the whole page stack.  It lives on the panel (instead of a
    /// Margin on the ItemsControl) so that the panel and the zoom controller
    /// can share one unambiguous content geometry.
    /// </summary>
    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<PdfVirtualizingPanel, Thickness>(nameof(Padding));

    private readonly Dictionary<int, Control> _realized = new();
    private readonly Stack<Control> _recyclePool = new();
    private readonly HashSet<int> _retainedForZoom = new();
    private double[] _basePageHeightOffsets = [];
    private bool _zoomViewportFrozen;
    private double _maxBaseItemWidth;
    private Rect _viewport;

    public PdfVirtualizingPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    static PdfVirtualizingPanel()
    {
        BufferFactorProperty.Changed.AddClassHandler<PdfVirtualizingPanel>(
            (panel, _) => panel.InvalidateMeasure());
        ZoomProperty.Changed.AddClassHandler<PdfVirtualizingPanel>(
            (panel, _) => panel.InvalidateMeasure());
        PaddingProperty.Changed.AddClassHandler<PdfVirtualizingPanel>(
            (panel, _) => panel.InvalidateMeasure());
    }

    public double BufferFactor
    {
        get => GetValue(BufferFactorProperty);
        set => SetValue(BufferFactorProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    internal int MeasurePassCount { get; private set; }
    internal int ScrollDirection { get; private set; } = 1;

    internal int CurrentPageIndex
    {
        get
        {
            if (Items.Count == 0 || _viewport.Height <= 0) return -1;

            double top = _viewport.Top - Padding.Top;
            int index = FindPageIndexAt(top, Zoom);
            if (index + 1 < Items.Count)
            {
                double nextPageTop = ItemTopAt(index + 1, Zoom);
                if (top >= nextPageTop - PdfPageViewModel.FooterHeight / 2) index++;
            }

            return index;
        }
    }

    internal bool IsContainerVisible(Control container)
    {
        int index = IndexFromContainer(container);
        if (index < 0 || _viewport.Height <= 0) return false;

        double top = _viewport.Top - Padding.Top;
        double bottom = top + _viewport.Height;
        return ItemTopAt(index + 1, Zoom) > top && ItemTopAt(index, Zoom) < bottom;
    }

    /// <summary>
    /// Horizontal offset of an item inside the panel's content area: pages are
    /// centred while they fit and left-aligned once they overflow.
    /// </summary>
    internal static double HorizontalItemOffset(double contentAreaWidth, double itemWidth) =>
        Math.Max(0, (contentAreaWidth - itemWidth) / 2);

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasurePassCount++;
        if (Items.Count == 0)
        {
            RecycleAll();
            _basePageHeightOffsets = [];
            return default;
        }

        EnsureBaseLayout();

        if (_viewport.Height <= 0 && double.IsFinite(availableSize.Height))
        {
            _viewport = new Rect(0, 0, availableSize.Width, availableSize.Height);
        }

        GetRealizationRange(out int firstIndex, out int lastIndex);
        foreach (int index in _realized.Keys
                     .Where(index => (index < firstIndex || index > lastIndex) &&
                                     !_retainedForZoom.Contains(index))
                     .ToArray())
        {
            Recycle(index);
        }

        for (int index = firstIndex; index <= lastIndex; index++)
        {
            Control container = GetOrCreate(index);
            var page = (PdfPageViewModel)Items[index]!;
            container.Measure(new Size(
                PdfPageViewModel.PageWidthAt(page, Zoom),
                PdfPageViewModel.ItemHeightAt(page, Zoom)));
        }

        Thickness padding = Padding;
        return new Size(
            MaxItemWidthAt(Zoom) + padding.Left + padding.Right,
            TotalItemHeightAt(Zoom) + padding.Top + padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Thickness padding = Padding;
        double contentWidth = Math.Max(0, finalSize.Width - padding.Left - padding.Right);

        foreach (var (index, container) in _realized)
        {
            var page = (PdfPageViewModel)Items[index]!;
            double width = PdfPageViewModel.PageWidthAt(page, Zoom);
            double height = PdfPageViewModel.ItemHeightAt(page, Zoom);
            double x = padding.Left + HorizontalItemOffset(contentWidth, width);
            container.Arrange(new Rect(x, padding.Top + ItemTopAt(index, Zoom), width, height));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items,
        NotifyCollectionChangedEventArgs e)
    {
        RecycleAll();
        _retainedForZoom.Clear();
        _zoomViewportFrozen = false;
        _basePageHeightOffsets = [];
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

        if (this.FindAncestorOfType<ScrollViewer>() is { } scrollViewer)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Padding.Top + ItemTopAt(index, Zoom));
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

        if (wrap)
        {
            if (toIndex < 0) toIndex = count - 1;
            else if (toIndex >= count) toIndex = 0;
        }

        return toIndex >= 0 && toIndex < count ? ScrollIntoView(toIndex) : null;
    }

    internal void BeginZoomAnimation()
    {
        _zoomViewportFrozen = false;
        _retainedForZoom.Clear();
        foreach (int index in _realized.Keys) _retainedForZoom.Add(index);
    }

    internal void FreezeZoomViewport() => _zoomViewportFrozen = true;

    internal void EndZoomAnimation()
    {
        _zoomViewportFrozen = false;
        _retainedForZoom.Clear();
        InvalidateMeasure();
    }

    private void EnsureBaseLayout()
    {
        if (_basePageHeightOffsets.Length == Items.Count + 1) return;

        _basePageHeightOffsets = new double[Items.Count + 1];
        _maxBaseItemWidth = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            var page = (PdfPageViewModel)Items[i]!;
            _maxBaseItemWidth = Math.Max(_maxBaseItemWidth, page.BaseWidth);
            _basePageHeightOffsets[i + 1] = _basePageHeightOffsets[i] + page.BaseHeight;
        }
    }

    internal double MaxItemWidthAt(double zoom)
    {
        EnsureBaseLayout();
        return Math.Max(1, _maxBaseItemWidth * zoom);
    }

    internal double TotalItemHeightAt(double zoom) => ItemTopAt(Items.Count, zoom);

    internal double ItemTopAt(int index, double zoom)
    {
        EnsureBaseLayout();
        return _basePageHeightOffsets[index] * zoom + PdfPageViewModel.FooterHeight * index;
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
        double top = _viewport.Top - Padding.Top;
        double start = Math.Max(0, top - buffer);
        double end = Math.Min(TotalItemHeightAt(Zoom), top + _viewport.Height + buffer);
        firstIndex = FindPageIndexAt(start, Zoom);
        lastIndex = firstIndex;
        while (lastIndex + 1 < Items.Count && ItemTopAt(lastIndex + 1, Zoom) < end)
        {
            lastIndex++;
        }
    }

    internal int FindPageIndexAt(double y, double zoom)
    {
        int low = 0;
        int high = Items.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (ItemTopAt(middle + 1, zoom) <= y) low = middle + 1;
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
        if (_zoomViewportFrozen) return;

        Rect previous = _viewport;
        Rect viewport = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        if (Math.Abs(viewport.Top - previous.Top) > 0.5)
            ScrollDirection = Math.Sign(viewport.Top - previous.Top);
        _viewport = viewport;
        if (Items.Count == 0 || _basePageHeightOffsets.Length != Items.Count + 1)
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
        double top = _viewport.Top - Padding.Top;
        double start = Math.Max(0, top - buffer);
        double end = Math.Min(TotalItemHeightAt(Zoom), top + _viewport.Height + buffer);
        int first = _realized.Keys.Min();
        int last = _realized.Keys.Max();
        return ItemTopAt(first, Zoom) <= start && ItemTopAt(last + 1, Zoom) >= end;
    }
}
