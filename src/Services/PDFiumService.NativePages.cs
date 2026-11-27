using PDFiumCore;
using pdf_fluent.Models;

namespace pdf_fluent.Services;

public partial class PDFiumService
{
    // PDFium's text page borrows its page. Keep them together, and always close
    // text before page before document. All access is serialized by _pageLock.
    internal const int MaxNativeTextPages = 8;
    private readonly Dictionary<(string File, int Index), NativeTextPage> _nativePages = new();
    private readonly LinkedList<(string File, int Index)> _nativePageLru = new();
    internal int NativeTextPageLoadCount { get; private set; }
    internal int LinkPageLoadCount { get; private set; }
    internal int CachedNativeTextPageCount { get { lock (_pageLock) return _nativePages.Count; } }

    private NativeTextPage GetNativeTextPage(string filePath, int pageIndex, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var key = (filePath, pageIndex);
        if (_nativePages.TryGetValue(key, out NativeTextPage? cached))
        {
            _nativePageLru.Remove(cached.Node);
            _nativePageLru.AddLast(cached.Node);
            return cached;
        }
        var document = GetOrLoadDocument(filePath);
        var page = fpdfview.FPDF_LoadPage(document, pageIndex)
            ?? throw new InvalidOperationException($"无法加载 PDF 第 {pageIndex + 1} 页。");
        FpdfTextpageT? text = null;
        try
        {
            text = fpdf_text.FPDFTextLoadPage(page)
                ?? throw new InvalidOperationException("无法加载 PDF 文本。");
            var result = new NativeTextPage(document, page, text,
                GetVisiblePageBox(document, pageIndex, page), fpdf_edit.FPDFPageGetRotation(page),
                new LinkedListNode<(string, int)>(key));
            cancellation.ThrowIfCancellationRequested();
            while (_nativePages.Count >= MaxNativeTextPages)
                ReleaseNativeTextPage(_nativePageLru.First!.Value);
            _nativePages.Add(key, result);
            _nativePageLru.AddLast(result.Node);
            NativeTextPageLoadCount++;
            return result;
        }
        catch
        {
            if (text is not null) fpdf_text.FPDFTextClosePage(text);
            fpdfview.FPDF_ClosePage(page);
            throw;
        }
    }

    private void ReleaseNativeTextPage((string File, int Index) key)
    {
        if (!_nativePages.Remove(key, out NativeTextPage? entry)) return;
        _nativePageLru.Remove(entry.Node);
        fpdf_text.FPDFTextClosePage(entry.Text);
        fpdfview.FPDF_ClosePage(entry.Page);
    }

    public PdfLinkPage GetPageLinks(string filePath, int pageNumber, CancellationToken cancellationToken = default)
    {
        lock (_pageLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeTextPage entry = GetNativeTextPage(filePath, pageNumber, cancellationToken);
            if (entry.Links is { } links) return links;
            var result = new PdfLinkPage(pageNumber, entry.CropBox,
                ReadPageLinks(entry.Document, entry.Page, entry.Text, cancellationToken), entry.Rotation);
            cancellationToken.ThrowIfCancellationRequested();
            entry.Links = result;
            LinkPageLoadCount++;
            return result;
        }
    }

    private sealed class NativeTextPage(FpdfDocumentT document, FpdfPageT page, FpdfTextpageT text,
        PdfRect cropBox, int rotation, LinkedListNode<(string File, int Index)> node)
    {
        public FpdfDocumentT Document { get; } = document;
        public FpdfPageT Page { get; } = page;
        public FpdfTextpageT Text { get; } = text;
        public PdfRect CropBox { get; } = cropBox;
        public int Rotation { get; } = rotation;
        public LinkedListNode<(string File, int Index)> Node { get; } = node;
        public PdfLinkPage? Links { get; set; }
    }
}
