using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class PdfDocumentView
{
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);
    private static readonly TimeSpan SelectionAutoScrollInterval = TimeSpan.FromMilliseconds(30);

    internal bool IsTextCursor => ReferenceEquals(DocumentScroller.Cursor, TextCursor);

    private DocumentTabViewModel? _textDocument;
    private DispatcherTimer? _selectionAutoScrollTimer;
    private PdfTextPosition _selectionAnchor;
    private Point _selectionPressPoint;
    private Point _lastSelectionPointer;
    private int _selectionPressCharacter;
    private int _selectionPressPage;
    private bool _isSelectingText;
    private bool _selectionMoved;
    private bool _selectionStartedOnHighlight;
    private IPointer? _selectionPointer;
    private MenuFlyout? _highlightContextMenu;

    internal MenuFlyout? HighlightContextMenu => _highlightContextMenu;

    private void InitializeTextInteraction()
    {
        var deleteItem = new MenuItem { Header = "删除高亮" };
        deleteItem.Click += OnDeleteHighlightClick;
        _highlightContextMenu = new UnselectedMenuFlyout
        {
            FlyoutPresenterTheme = this.FindResource("MoreActionsMenuPresenterTheme") as ControlTheme,
            ItemContainerTheme = this.FindResource("MoreActionsMenuItemTheme") as ControlTheme,
        };
        _highlightContextMenu.Items.Add(deleteItem);
        AddHandler(PointerPressedEvent, OnTextPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnTextPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnTextPointerReleased, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnTextKeyDown, RoutingStrategies.Tunnel);
        PointerExited += OnTextPointerExited;
    }

    private void AttachTextDocument(DocumentTabViewModel? document)
    {
        if (ReferenceEquals(_textDocument, document)) return;
        CancelTextSelection();
        if (_textDocument is not null)
            _textDocument.SearchResultNavigationRequested -= NavigateToSearchResult;
        _textDocument = document;
        if (_textDocument is not null)
            _textDocument.SearchResultNavigationRequested += NavigateToSearchResult;
    }

    private void DetachTextInteraction()
    {
        CancelTextSelection();
        _highlightContextMenu?.Hide();
        AttachTextDocument(null);
        DocumentScroller.Cursor = DefaultCursor;
    }

    private void OnTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document || !IsPageContent(e.Source)) return;
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            SelectHighlightAtPointer(e, document, showContextMenu: true);
            return;
        }
        if (!properties.IsLeftButtonPressed) return;

        bool startedOnHighlight = SelectHighlightAtPointer(e, document, showContextMenu: false);
        if (!TryGetTextPosition(e, nearestOnLine: false, out PageTextPosition position) || !position.Hit.IsOnText)
        {
            if (TryGetPagePoint(e, out _, out _)) document.ClearSelection();
            return;
        }

        _isSelectingText = true;
        _selectionMoved = false;
        _selectionStartedOnHighlight = startedOnHighlight;
        _selectionAnchor = new PdfTextPosition(position.PageIndex, position.Hit.CaretIndex);
        _selectionPressCharacter = position.Hit.CharacterIndex;
        _selectionPressPage = position.PageIndex;
        _selectionPressPoint = e.GetPosition(DocumentScroller);
        _lastSelectionPointer = _selectionPressPoint;
        document.ClearSelection();
        document.ActiveSearchResult = null;
        DocumentScroller.Focus();
        _selectionPointer = e.Pointer;
        e.Pointer.Capture(DocumentScroller);
        StartSelectionAutoScroll();
        e.Handled = true;
    }

    private bool SelectHighlightAtPointer(
        PointerEventArgs e,
        DocumentTabViewModel document,
        bool showContextMenu)
    {
        document.SelectedHighlight = null;
        if (TryGetPagePoint(e, out int pageIndex, out Point point) &&
            document.HitTestHighlight(pageIndex, point) is { } highlight)
        {
            document.SelectedHighlight = highlight;
            if (showContextMenu)
                _highlightContextMenu?.ShowAt(DocumentScroller, showAtPointer: true);
            else
                _highlightContextMenu?.Hide();
            e.Handled = true;
            return true;
        }

        _highlightContextMenu?.Hide();
        e.Handled = showContextMenu;
        return false;
    }

    private void OnDeleteHighlightClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentTabViewModel { SelectedHighlight: { } highlight } document)
            document.RemoveHighlight(highlight);
        _highlightContextMenu?.Hide();
    }

    private void OnTextPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        if (_isSelectingText)
        {
            Vector movement = e.GetPosition(DocumentScroller) - _selectionPressPoint;
            if (_selectionStartedOnHighlight && !_selectionMoved && movement.Length <= 3)
            {
                e.Handled = true;
                return;
            }
            if (movement.Length > 3)
            {
                if (_selectionStartedOnHighlight) document.SelectedHighlight = null;
                _selectionMoved = true;
            }
            _lastSelectionPointer = e.GetPosition(DocumentScroller);
            UpdateTextSelection(e);
            e.Handled = true;
            return;
        }

        bool overText = TryGetTextPosition(e, nearestOnLine: false, out PageTextPosition position) &&
                        position.Hit.IsOnText;
        DocumentScroller.Cursor = overText ? TextCursor : DefaultCursor;
    }

    private async void OnTextPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelectingText || DataContext is not DocumentTabViewModel document) return;
        if (_selectionStartedOnHighlight && !_selectionMoved)
        {
            CancelTextSelection();
            document.ClearSelection();
            e.Handled = true;
            return;
        }

        UpdateTextSelection(e);
        PdfTextRange? selection = document.Selection;
        bool highlight = document.IsHighlightMode;
        bool moved = _selectionMoved || selection is not null;
        int pressPage = _selectionPressPage;
        int pressCharacter = _selectionPressCharacter;
        CancelTextSelection();
        e.Handled = true;

        if (!highlight)
        {
            if (!moved) document.ClearSelection();
            return;
        }

        try
        {
            if (selection is not null)
                await document.AddHighlightAsync(selection.Value);
            else
                await document.AddWordHighlightAsync(pressPage, pressCharacter);
        }
        catch (Exception ex)
        {
            document.HighlightErrorMessage = ex.Message;
        }
    }

    private void UpdateTextSelection(PointerEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        if (TryGetTextPosition(e, nearestOnLine: true, out PageTextPosition position))
        {
            var current = new PdfTextPosition(position.PageIndex, position.Hit.CaretIndex);
            document.UpdateSelection(_selectionAnchor, current);
            if (PdfTextPosition.Compare(_selectionAnchor, current) != 0) _selectionMoved = true;
        }
    }

    private bool TryGetPagePoint(PointerEventArgs e, out int pageIndex, out Point point)
    {
        foreach (Control container in PageItems.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageViewModel page) continue;
            PdfPageImage? image = container.GetVisualDescendants().OfType<PdfPageImage>().FirstOrDefault();
            if (image is null) continue;

            point = e.GetPosition(image);
            if (image.Geometry.Bounds.Contains(point))
            {
                point = image.Geometry.ToLogical(point);
                pageIndex = page.PageIndex;
                return true;
            }
        }

        pageIndex = -1;
        point = default;
        return false;
    }

    private bool TryGetTextPosition(
        PointerEventArgs e,
        bool nearestOnLine,
        out PageTextPosition result)
    {
        if (DataContext is DocumentTabViewModel document)
        {
            foreach (Control container in PageItems.GetRealizedContainers())
            {
                if (container.DataContext is not PdfPageViewModel page) continue;
                PdfPageImage? image = container.GetVisualDescendants().OfType<PdfPageImage>().FirstOrDefault();
                if (image is null) continue;

                Point point = e.GetPosition(image);
                if (!image.Geometry.Bounds.Contains(point)) continue;
                point = image.Geometry.ToLogical(point);

                PdfTextHit? hit = document.HitTestText(page.PageIndex, point, nearestOnLine);
                if (hit is not null)
                {
                    result = new PageTextPosition(page.PageIndex, hit.Value);
                    return true;
                }

            }
        }

        result = default;
        return false;
    }

    private void StartSelectionAutoScroll()
    {
        _selectionAutoScrollTimer ??= new DispatcherTimer { Interval = SelectionAutoScrollInterval };
        _selectionAutoScrollTimer.Tick -= OnSelectionAutoScroll;
        _selectionAutoScrollTimer.Tick += OnSelectionAutoScroll;
        _selectionAutoScrollTimer.Start();
    }

    private void OnSelectionAutoScroll(object? sender, EventArgs e)
    {
        if (!_isSelectingText) return;
        double height = DocumentScroller.Bounds.Height;
        double delta = 0;
        if (_lastSelectionPointer.Y < 24)
            delta = -Math.Max(4, (24 - _lastSelectionPointer.Y) * 0.8);
        else if (_lastSelectionPointer.Y > height - 24)
            delta = Math.Max(4, (_lastSelectionPointer.Y - (height - 24)) * 0.8);
        if (delta == 0) return;

        Vector offset = DocumentScroller.Offset;
        DocumentScroller.Offset = new Vector(offset.X, Math.Max(0, offset.Y + delta));
        UpdateSelectionAtCapturedPointer();
    }

    private void UpdateSelectionAtCapturedPointer()
    {
        if (DataContext is not DocumentTabViewModel document) return;
        Point documentPoint = DocumentScroller.TranslatePoint(_lastSelectionPointer, PageItems) ?? _lastSelectionPointer;
        foreach (Control container in PageItems.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageViewModel page ||
                container.TranslatePoint(new Point(), PageItems) is not { } origin)
                continue;
            var bounds = new Rect(origin, container.Bounds.Size);
            if (!bounds.Contains(documentPoint) || page.Text is null) continue;

            int caret = documentPoint.Y >= origin.Y + container.Bounds.Height / 2
                ? page.Text.CharacterCount
                : 0;
            document.UpdateSelection(_selectionAnchor, new PdfTextPosition(page.PageIndex, caret));
            _selectionMoved = true;
            return;
        }
    }

    private void CancelTextSelection()
    {
        _isSelectingText = false;
        _selectionStartedOnHighlight = false;
        _selectionAutoScrollTimer?.Stop();
        _selectionPointer?.Capture(null);
        _selectionPointer = null;
    }

    private void OnTextPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isSelectingText) DocumentScroller.Cursor = DefaultCursor;
    }

    private async void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        if (e.Key == Key.Delete && document.SelectedHighlight is { } highlight && !IsTextBoxInput(e.Source))
        {
            document.RemoveHighlight(highlight);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            document.ShowSearch();
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C &&
            (document.HasSelection || document.SelectedHighlight is not null) && !IsTextBoxInput(e.Source))
        {
            e.Handled = true;
            string text = document.HasSelection
                ? await document.GetSelectedTextAsync()
                : await document.GetSelectedHighlightTextAsync();
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && text.Length > 0)
                await clipboard.SetTextAsync(text);
            return;
        }

        if (e.Key == Key.Escape && !IsTextBoxInput(e.Source))
        {
            document.ClearSelection();
            document.IsHighlightMode = false;
            e.Handled = true;
        }
    }

    private static bool IsTextBoxInput(object? source) =>
        source is Visual visual &&
        (visual is TextBox || visual.GetVisualAncestors().OfType<TextBox>().Any());

    private bool IsPageContent(object? source) =>
        source is Visual visual &&
        (ReferenceEquals(visual, PageItems) ||
         visual.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, PageItems)));

    private void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        document.ShowSearch();
        SearchTextBox.Focus();
    }

    private void OnCloseSearchClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentTabViewModel document) document.HideSearch();
        DocumentScroller.Focus();
    }

    private void OnSearchResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DocumentTabViewModel document &&
            SearchResultsList.SelectedItem is PdfSearchResult result)
        {
            document.GoToSearchResult(result);
        }
    }

    private void NavigateToSearchResult(PdfSearchResult result)
    {
        if (DataContext is not DocumentTabViewModel document ||
            document.GetLoadedText(result.PageIndex) is not { } text)
            return;
        IReadOnlyList<PdfRect> rects = text.GetMergedRects(result.CharacterIndex, result.CharacterCount);
        if (rects.Count == 0) return;

        PdfVirtualizingPanel? panel = PageItems.GetVisualDescendants()
            .OfType<PdfVirtualizingPanel>()
            .FirstOrDefault();
        if (panel is null) return;
        double scale = DocumentTabViewModel.LayoutScale * document.Zoom;
        double matchY = (text.CropBox.T - rects[0].T) * scale;
        double y = panel.Padding.Top + panel.ItemTopAt(result.PageIndex, document.Zoom) + matchY;
        DocumentScroller.Offset = new Vector(
            DocumentScroller.Offset.X,
            Math.Max(0, y - Math.Min(matchY, DocumentScroller.Bounds.Height / 3)));
    }

    private readonly record struct PageTextPosition(int PageIndex, PdfTextHit Hit);
}
