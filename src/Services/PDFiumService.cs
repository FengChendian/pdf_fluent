using PDFiumCore;
using PDFiumCore.Delegates;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using pdf_fluent.Diagnostics;
using pdf_fluent.Models;

namespace pdf_fluent.Services
{
    public partial class PDFiumService : IDisposable, IPdfDocumentService
    {
        private bool _disposed;

        // ── Library lifecycle (reference-counted — init once, destroy on last Dispose) ──
        private static int _libraryRefCount;
        private static readonly object _libraryLock = new();

        // Native page/text pairs are bounded and share this document cache.
        private readonly ConcurrentDictionary<string, FpdfDocumentT> _pdfDocumentCache = new();

        public PDFiumService()
        {
            StartupTrace.Mark("PDFiumService.ctor.enter");
            lock (_libraryLock)
            {
                if (_libraryRefCount == 0)
                    fpdfview.FPDF_InitLibrary();
                _libraryRefCount++;
            }
            StartupTrace.Mark("PDFiumService.ctor.exit (FPDF_InitLibrary)");
        }

        private int _documentOpenCount;
        internal int DocumentOpenCount => _documentOpenCount;

        private static FpdfDocumentT LoadDocumentCore(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("PDF 文件不存在。", filePath);

            lock (_pageLock)
            {
                var document = fpdfview.FPDF_LoadDocument(filePath, null);
                if (document == null)
                    throw new InvalidOperationException($"无法打开 PDF 文档：{filePath}");
                return document;
            }
        }

        private FpdfDocumentT GetOrLoadDocument(string filePath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pdfDocumentCache.TryGetValue(filePath, out var cached)) return cached;

            lock (_pageLock)
            {
                if (_pdfDocumentCache.TryGetValue(filePath, out cached)) return cached;
                var document = LoadDocumentCore(filePath);
                _pdfDocumentCache[filePath] = document;
                _documentOpenCount++;
                return document;
            }
        }

        private FpdfDocumentT LoadDocument(string filePath)
        {
            var document = LoadDocumentCore(filePath);
            _documentOpenCount++;
            return document;
        }

        public int GetPageCount(string filePath)
        {
            lock (_pageLock)
                return fpdfview.FPDF_GetPageCount(GetOrLoadDocument(filePath));
        }

        public (double Width, double Height) GetPageSize(string filePath, int pageNumber)
        {
            double width = 0, height = 0;
            lock (_pageLock)
            {
                var document = GetOrLoadDocument(filePath);
                fpdfview.FPDF_GetPageSizeByIndex(document, pageNumber, ref width, ref height);
            }

            return (width, height);
        }

        public (double Width, double Height)[] GetPageSizes(string filePath)
        {
            StartupTrace.Mark("PDFiumService.GetPageSizes.enter");
            lock (_pageLock)
            {
                var document = GetOrLoadDocument(filePath);
                int pageCount = fpdfview.FPDF_GetPageCount(document);
                var sizes = new (double Width, double Height)[pageCount];
                for (int i = 0; i < pageCount; i++)
                {
                    double width = 0;
                    double height = 0;
                    fpdfview.FPDF_GetPageSizeByIndex(document, i, ref width, ref height);
                    sizes[i] = (width, height);
                }

                StartupTrace.Mark($"PDFiumService.GetPageSizes.exit ({pageCount} pages)");
                return sizes;
            }
        }

        /// <summary>Renders premultiplied BGRA pixels, with alpha 255 on opaque pages.</summary>
        public (int width, int height, byte[] pixels) RenderPageToRawBuffer(
            string filePath,
            int pageNumber = 0,
            float scale = 1f)
        {
            lock (_pageLock)
                return RenderSinglePageToBuffer(GetOrLoadDocument(filePath), pageNumber, scale);
        }

        /// <summary>
        /// Renders multiple pages into premultiplied BGRA byte buffers.
        /// </summary>
        public (int width, int height, byte[] pixels)[] RenderPagesToRawBuffers(
            string filePath,
            int[] pageNumbers,
            float scale = 1f)
        {
            var rawBuffers = new (int width, int height, byte[] pixels)[pageNumbers.Length];
            RenderPagesToRawBuffersInternal(filePath, pageNumbers, rawBuffers, scale);
            return rawBuffers;
        }

        /// <summary>
        /// Renders directly into a bitmap that is not yet attached to the visual tree.
        /// The completed bitmap can be published on the UI thread without a pixel copy.
        /// </summary>
        public WriteableBitmap RenderPageToWriteableBitmap(
            string filePath,
            int pageNumber = 0,
            float scale = 1f,
            CancellationToken cancellationToken = default,
            PixelSize? targetPixelSize = null)
        {
            if (targetPixelSize is { } size && (size.Width <= 0 || size.Height <= 0))
                throw new ArgumentOutOfRangeException(nameof(targetPixelSize));
            WriteableBitmap result;
            bool needsPremultiplication = false;
            lock (_pageLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = GetOrLoadDocument(filePath);
                StartupTrace.Mark($"PDFiumService.RenderPageToWriteableBitmap.enter (page {pageNumber})");
                result = RenderSinglePage(document, pageNumber, scale, (page, width, height, hasTransparency) =>
                {
                    needsPremultiplication = hasTransparency;
                    cancellationToken.ThrowIfCancellationRequested();
                    var bitmap = new WriteableBitmap(
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        hasTransparency ? AlphaFormat.Premul : AlphaFormat.Opaque);
                    try
                    {
                        using (var target = bitmap.Lock())
                        {
                            RenderPageIntoBuffer(
                                page, width, height, target.Address, target.RowBytes, hasTransparency, cancellationToken);
                        }

                        return bitmap;
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                }, targetPixelSize);
            }

            try
            {
                // Native handles are closed now. CPU-only conversion must not
                // block text extraction or other documents on PDFium's lock.
                if (needsPremultiplication)
                {
                    using var target = result.Lock();
                    PremultiplyBitmap(target.Address, result.PixelSize.Width, result.PixelSize.Height,
                        target.RowBytes, cancellationToken);
                }
                StartupTrace.Mark($"PDFiumService.RenderPageToWriteableBitmap.exit (page {pageNumber})");
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Batch bitmap convenience
        // ════════════════════════════════════════════════════════════════

        public Dictionary<int, WriteableBitmap> RenderPagesToWriteableBitmaps(
            string filePath,
            int[] pageNumbers,
            float scale = 1f)
        {
            var rawBuffers = RenderPagesToRawBuffers(filePath, pageNumbers, scale);
            var result = new Dictionary<int, WriteableBitmap>(pageNumbers.Length);
            for (int i = 0; i < pageNumbers.Length; i++)
            {
                var (w, h, p) = rawBuffers[i];
                result[pageNumbers[i]] = CreateWriteableBitmapFromRawBuffer(w, h, p);
            }
            return result;
        }

        public void RenderPagesToWriteableBitmaps(
            string filePath,
            int[] pageNumbers,
            WriteableBitmap[] outputBitmaps,
            float scale = 1f)
        {
            if (outputBitmaps.Length < pageNumbers.Length)
                throw new ArgumentException(
                    $"Output array size ({outputBitmaps.Length}) is smaller than page count ({pageNumbers.Length}).",
                    nameof(outputBitmaps));

            var rawBuffers = RenderPagesToRawBuffers(filePath, pageNumbers, scale);
            for (int i = 0; i < pageNumbers.Length; i++)
            {
                var (w, h, p) = rawBuffers[i];
                outputBitmaps[i] = CreateWriteableBitmapFromRawBuffer(w, h, p);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Core rendering (raw bytes only, no UI types)
        // ════════════════════════════════════════════════════════════════

        private void RenderPagesToRawBuffersInternal(
            string filePath,
            int[] pageNumbers,
            (int width, int height, byte[] pixels)[] output,
            float scale)
        {
            if (pageNumbers.Length == 0) return;

            lock (_pageLock)
            {
                var document = GetOrLoadDocument(filePath);
                for (int i = 0; i < pageNumbers.Length; i++)
                    output[i] = RenderSinglePageToBuffer(document, pageNumbers[i], scale);
            }
        }

        /// <remarks>
        /// PDFium is not internally thread-safe. The same lock also keeps a cached
        /// document alive until the current native operation has finished.
        /// </remarks>
        private static readonly object _pageLock = new();

        internal static (int width, int height, byte[] pixels) RenderSinglePageToBuffer(
            FpdfDocumentT document,
            int pageNumber,
            float scale)
        {
            return RenderSinglePage(document, pageNumber, scale, (page, width, height, hasTransparency) =>
            {
                byte[] pixels = new byte[checked(width * height * 4)];
                unsafe
                {
                    fixed (byte* buffer = pixels)
                    {
                        RenderPageIntoBuffer(
                            page, width, height, (IntPtr)buffer, width * 4, hasTransparency, CancellationToken.None);
                        if (hasTransparency)
                            PremultiplyBitmap((IntPtr)buffer, width, height, width * 4, CancellationToken.None);
                    }
                }

                // BGRx's fourth byte is unused by PDFium. The raw-buffer API
                // promises BGRA, so make its alpha explicit before publishing.
                if (!hasTransparency)
                    for (int offset = 3; offset < pixels.Length; offset += 4)
                        pixels[offset] = byte.MaxValue;

                return (width, height, pixels);
            });
        }

        private static T RenderSinglePage<T>(
            FpdfDocumentT document,
            int pageNumber,
            float scale,
            Func<FpdfPageT, int, int, bool, T> render,
            PixelSize? targetPixelSize = null)
        {
            FpdfPageT page;
            int width;
            int height;
            lock (_pageLock)
            {
                double pageWidth = 0;
                double pageHeight = 0;
                page = fpdfview.FPDF_LoadPage(document, pageNumber)
                       ?? throw new InvalidOperationException($"无法加载 PDF 第 {pageNumber + 1} 页。");
                HideHighlightAnnotations(page);
                fpdfview.FPDF_GetPageSizeByIndex(document, pageNumber, ref pageWidth, ref pageHeight);
                PixelSize pixels = targetPixelSize ?? PdfPageGeometry.GetPixelSize(pageWidth, pageHeight, scale);
                width = pixels.Width;
                height = pixels.Height;
            }

            try
            {
                // Like Chromium, ask whether PDFium needs background alpha;
                // simple object opacity alone need not require an alpha target.
                bool hasTransparency;
                lock (_pageLock) hasTransparency = fpdf_edit.FPDFPageHasTransparency(page) != 0;
                return render(page, width, height, hasTransparency);
            }
            finally
            {
                lock (_pageLock) fpdfview.FPDF_ClosePage(page);
            }
        }

        private static void RenderPageIntoBuffer(
            FpdfPageT page,
            int width,
            int height,
            IntPtr buffer,
            int stride,
            bool hasTransparency,
            CancellationToken cancellationToken)
        {
            const int renderToBeContinued = 1;
            const int renderDone = 2;

            lock (_pageLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Match Chromium: alpha targets disable AGG's LCD subpixel
                // output, even when every pixel was initially filled white.
                var format = hasTransparency ? FPDFBitmapFormat.BGRA : FPDFBitmapFormat.BGRx;
                var bitmap = fpdfview.FPDFBitmapCreateEx(
                    width, height, (int)format, buffer, stride);
                if (bitmap == null)
                    throw new InvalidOperationException("无法创建 PDFium 位图。");

                // PDFiumCore stores only the native function pointer in
                // IFSDK_PAUSE.NeedToPauseNow; it does not keep the managed
                // delegate alive. Hold a local reference (and KeepAlive it
                // after the last native call) so the GC cannot collect the
                // thunk while PDFium is still calling it. Without this the
                // process can FailFast with "callback was made on a garbage
                // collected delegate", especially with ReadyToRun timing.
                Func_int___IntPtr needToPauseNow = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
                using var pause = new IFSDK_PAUSE { Version = 1 };
                pause.NeedToPauseNow = needToPauseNow;
                try
                {
                    // Blend against transparency first; the page control draws
                    // the white paper underneath the completed bitmap.
                    uint clearColor = hasTransparency ? 0u : uint.MaxValue;
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, clearColor);
                    int status = fpdf_progressive.FPDF_RenderPageBitmapStart(
                        bitmap, page, 0, 0, width, height, 0,
                        (int)(RenderFlags.RenderAnnotations | RenderFlags.OptimizeTextForLcd), pause);
                    while (status == renderToBeContinued && !cancellationToken.IsCancellationRequested)
                        status = fpdf_progressive.FPDF_RenderPageContinue(page, pause);

                    cancellationToken.ThrowIfCancellationRequested();
                    if (status != renderDone)
                        throw new InvalidOperationException($"PDFium 渲染失败，状态码：{status}。");
                }
                finally
                {
                    fpdf_progressive.FPDF_RenderPageClose(page);
                    fpdfview.FPDFBitmapDestroy(bitmap);
                    GC.KeepAlive(needToPauseNow);
                    GC.KeepAlive(pause);
                }
            }
        }

        internal static unsafe void PremultiplyBitmap(
            IntPtr buffer, int width, int height, int stride, CancellationToken cancellationToken)
        {
            // Work on packed channels rather than dividing each color byte.
            // B/R occupy independent 16-bit lanes; (n + 128 + ((n + 128)
            // >> 8)) >> 8 is exact rounded division by 255 for byte products.
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte* pixel = (byte*)buffer + (long)y * stride;
                for (int x = 0; x < width; x++, pixel += 4)
                {
                    if (BitConverter.IsLittleEndian)
                    {
                        uint bgra = *(uint*)pixel;
                        uint alpha = bgra >> 24;
                        if (alpha == 255) continue;
                        uint rb = (bgra & 0x00FF00FFu) * alpha + 0x00800080u;
                        uint g = (bgra & 0x0000FF00u) * alpha + 0x00008000u;
                        rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
                        g = ((g + ((g >> 8) & 0x0000FF00u)) >> 8) & 0x0000FF00u;
                        *(uint*)pixel = rb | g | (bgra & 0xFF000000u);
                    }
                    else
                    {
                        int alpha = pixel[3];
                        if (alpha == 255) continue;
                        pixel[0] = (byte)((pixel[0] * alpha + 127) / 255);
                        pixel[1] = (byte)((pixel[1] * alpha + 127) / 255);
                        pixel[2] = (byte)((pixel[2] * alpha + 127) / 255);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        // ════════════════════════════════════════════════════════════════
        //  Bitmap helpers
        // ════════════════════════════════════════════════════════════════

        public void ReleaseDocument(string filePath)
        {
            lock (_pageLock)
            {
                ReleaseTextPagesForFile(filePath);
                if (_pdfDocumentCache.TryRemove(filePath, out var document))
                    fpdfview.FPDF_CloseDocument(document);
            }
        }

        /// <summary>Creates a <see cref="WriteableBitmap"/> from premultiplied BGRA pixel data.</summary>
        public static WriteableBitmap CreateWriteableBitmapFromRawBuffer(int width, int height, byte[] pixelData)
        {
            var expectedLength = checked(width * height * 4);
            if (pixelData.Length != expectedLength)
                throw new ArgumentException("BGRA 像素数据长度与位图尺寸不一致。", nameof(pixelData));

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var target = bitmap.Lock())
            {
                int rowBytes = width * 4;

                if (target.RowBytes == rowBytes)
                {
                    // Tightly packed: a single interop copy is several times
                    // faster than one Marshal.Copy per row, which matters a lot
                    // on HiDPI screens where every page is tens of megabytes.
                    Marshal.Copy(pixelData, 0, target.Address, pixelData.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(
                            pixelData,
                            y * rowBytes,
                            target.Address + y * target.RowBytes,
                            rowBytes);
                    }
                }
            }

            return bitmap;
        }

        // ════════════════════════════════════════════════════════════════
        //  Text page APIs (text selection / highlighting)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Loads a text page for the given PDF page.  Opens the document
        /// on first call and caches both the document handle and the text
        /// page.  Call <see cref="ReleaseTextPage"/> when done.
        /// The returned handle is borrowed; subsequent service calls may evict it.
        /// </summary>
        public FpdfTextpageT LoadTextPage(string filePath, int pageIndex)
        {
            lock (_pageLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return GetNativeTextPage(filePath, pageIndex, default).Text;
            }
        }

        /// <summary>
        /// Releases a single cached text page.
        /// </summary>
        public void ReleaseTextPage(string filePath, int pageIndex)
        {
            lock (_pageLock) ReleaseNativeTextPage((filePath, pageIndex));
        }

        /// <summary>
        /// Releases all cached text pages and document handles.
        /// Called when the PDF viewer page is unloaded.
        /// </summary>
        public void ReleaseAllTextPages()
        {
            lock (_pageLock)
            {
                foreach (var key in _nativePages.Keys.ToArray()) ReleaseNativeTextPage(key);

                foreach (var kv in _pdfDocumentCache)
                    fpdfview.FPDF_CloseDocument(kv.Value);
                _pdfDocumentCache.Clear();
            }
        }

        /// <summary>
        /// Releases all text pages for a given file (keeps the document
        /// open if it may be reused).
        /// </summary>
        public void ReleaseTextPagesForFile(string filePath)
        {
            lock (_pageLock)
            {
                foreach (var key in _nativePages.Keys.Where(key => key.File == filePath).ToArray())
                    ReleaseNativeTextPage(key);
            }
        }

        /// <summary>
        /// Extracts immutable managed text and geometry. PDFium character indices
        /// remain the canonical reading-order positions used by selection,
        /// copying, search results and text-markup annotations.
        /// </summary>
        public PdfTextPage GetPageText(
            string filePath,
            int pageIndex,
            CancellationToken cancellationToken = default)
        {
            PdfTextCharacter[] characters;
            PdfRect cropBox;
            lock (_pageLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                NativeTextPage entry = GetNativeTextPage(filePath, pageIndex, cancellationToken);
                FpdfTextpageT textPage = entry.Text;
                int count = Math.Max(0, fpdf_text.FPDFTextCountChars(textPage));
                characters = new PdfTextCharacter[count];
                for (int index = 0; index < count; index++)
                {
                    if ((index & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    double left = 0, right = 0, bottom = 0, top = 0;
                    PdfRect bounds = fpdf_text.FPDFTextGetCharBox(
                        textPage, index, ref left, ref right, ref bottom, ref top) != 0
                        ? new PdfRect(left, top, right, bottom)
                        : default;

                    PdfRect looseBounds = bounds;
                    using (var loose = new FS_RECTF_())
                    {
                        if (fpdf_text.FPDFTextGetLooseCharBox(textPage, index, loose) != 0)
                            looseBounds = new PdfRect(loose.Left, loose.Top, loose.Right, loose.Bottom);
                    }

                    characters[index] = new PdfTextCharacter(
                        index,
                        fpdf_text.FPDFTextGetUnicode(textPage, index),
                        bounds,
                        looseBounds);
                }
                cropBox = entry.CropBox;
            }

            return new PdfTextPage(pageIndex, cropBox, characters);
        }

        private static PdfRect GetVisiblePageBox(FpdfDocumentT document, int pageIndex, FpdfPageT page)
        {
            using var bounds = new FS_RECTF_();
            if (fpdfview.FPDF_GetPageBoundingBox(page, bounds) != 0 &&
                bounds.Right > bounds.Left && bounds.Top > bounds.Bottom)
            {
                return new PdfRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            }

            double width = 0, height = 0;
            fpdfview.FPDF_GetPageSizeByIndex(document, pageIndex, ref width, ref height);
            return new PdfRect(0, height, width, 0);
        }

        // ── Lightweight wrappers (delegate directly to fpdf_text) ─────

        public static int TextGetCharIndexAtPos(FpdfTextpageT textPage, double x, double y, double xTol, double yTol)
            => fpdf_text.FPDFTextGetCharIndexAtPos(textPage, x, y, xTol, yTol);

        public static int TextCountRects(FpdfTextpageT textPage, int startIndex, int count)
            => fpdf_text.FPDFTextCountRects(textPage, startIndex, count);

        public static bool TextGetRect(FpdfTextpageT textPage, int rectIndex, out double left, out double top, out double right, out double bottom)
        {
            left = top = right = bottom = 0;
            return fpdf_text.FPDFTextGetRect(textPage, rectIndex, ref left, ref top, ref right, ref bottom) != 0;
        }

        public static int TextCountChars(FpdfTextpageT textPage)
            => fpdf_text.FPDFTextCountChars(textPage);

        /// <summary>
        /// Extracts the selected text (UTF-16) and returns it as a .NET string.
        /// </summary>
        public static unsafe string TextGetText(FpdfTextpageT textPage, int startIndex, int count)
        {
            if (count <= 0) return string.Empty;

            var buffer = new ushort[count + 1]; // +1 for null terminator
            int written;
            fixed (ushort* ptr = buffer)
            {
                written = fpdf_text.FPDFTextGetText(textPage, startIndex, count, ref ptr[0]);
            }

            if (written <= 0) return string.Empty;

            // written includes the null terminator
            int charCount = written - 1;
            if (charCount <= 0) return string.Empty;

            return new string(
                Array.ConvertAll(buffer.AsSpan(0, charCount).ToArray(), u => (char)u));
        }

        /// <summary>
        /// Gets page dimensions in PDF points (1/72 inch) from an
        /// already-open document.
        /// </summary>
        public static (double Width, double Height) GetPageSizePt(FpdfDocumentT document, int pageIndex)
        {
            double w = 0, h = 0;
            fpdfview.FPDF_GetPageSizeByIndex(document, pageIndex, ref w, ref h);
            return (w, h);
        }

        // ════════════════════════════════════════════════════════════════
        //  Page bounding-box (CropBox / MediaBox origins)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the visible bounding box (left, bottom, right, top) for every
        /// page in the document. This is the intersection of the MediaBox and
        /// CropBox. The origin (<c>left</c>, <c>bottom</c>) is essential for
        /// correct coordinate mapping when the visible page area does not start
        /// at (0, 0).
        /// </summary>
        public (double Left, double Bottom, double Right, double Top)[] GetAllPageBBoxes(
            string filePath)
        {
            var document = LoadDocument(filePath);
            try
            {
                int count = fpdfview.FPDF_GetPageCount(document);
                var result = new (double, double, double, double)[count];
                for (int i = 0; i < count; i++)
                {
                    FpdfPageT page;
                    lock (_pageLock)
                    {
                        page = fpdfview.FPDF_LoadPage(document, i);
                    }
                    try
                    {
                        PdfRect bounds = GetVisiblePageBox(document, i, page);
                        result[i] = (bounds.L, bounds.B, bounds.R, bounds.T);
                    }
                    finally
                    {
                        lock (_pageLock)
                        {
                            fpdfview.FPDF_ClosePage(page);
                        }
                    }
                }
                return result;
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Annotations (highlight) & bookmarks (TOC)
        // ════════════════════════════════════════════════════════════════

        private const int FPDF_ANNOT_HIGHLIGHT = 9;
        private const int FPDF_ANNOT_FLAG_HIDDEN = 1 << 1;
        private const int PDFACTION_GOTO = 1;

        /// <summary>
        /// Sets the HIDDEN flag on all highlight annotations of a page so
        /// PDFium does not render them into the bitmap. Only affects the cached
        /// rendering document; the file and transient editing instances are
        /// untouched. Caller must hold
        /// <see cref="_pageLock"/>.
        /// </summary>
        private static void HideHighlightAnnotations(FpdfPageT page)
        {
            int count = fpdf_annot.FPDFPageGetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                if (annot == null) continue;
                if (fpdf_annot.FPDFAnnotGetSubtype(annot) == FPDF_ANNOT_HIGHLIGHT)
                    fpdf_annot.FPDFAnnotSetFlags(annot, FPDF_ANNOT_FLAG_HIDDEN);
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }

        // ── Bookmarks (table of contents) ─────────────────────────────

        /// <summary>Loads the document outline as a tree. Empty when the document has no bookmarks.</summary>
        public List<TocItem> GetBookmarks(string filePath)
        {
            var result = new List<TocItem>();
            var document = LoadDocument(filePath);
            try
            {
                lock (_pageLock)
                {
                    var root = fpdf_doc.FPDFBookmarkGetFirstChild(document, null);
                    BuildBookmarks(document, root, result);
                }
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
            return result;
        }

        private static void BuildBookmarks(
            FpdfDocumentT document, FpdfBookmarkT first, List<TocItem> list)
        {
            var bookmark = first;
            while (bookmark != null)
            {
                var item = new TocItem
                {
                    Title = GetBookmarkTitle(bookmark),
                    PageIndex = GetBookmarkPageIndex(document, bookmark),
                };
                list.Add(item);

                var child = fpdf_doc.FPDFBookmarkGetFirstChild(document, bookmark);
                if (child != null)
                    BuildBookmarks(document, child, item.Children);

                bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(document, bookmark);
            }
        }

        private static string GetBookmarkTitle(FpdfBookmarkT bookmark)
        {
            ulong len = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
            if (len <= 2) return string.Empty;

            var buffer = Marshal.AllocHGlobal((int)len);
            try
            {
                fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, len);
                // UTF-16LE, includes null terminator.
                return Marshal.PtrToStringUni(buffer, (int)len / 2 - 1) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static int GetBookmarkPageIndex(FpdfDocumentT document, FpdfBookmarkT bookmark)
        {
            var dest = fpdf_doc.FPDFBookmarkGetDest(document, bookmark);
            if (dest == null)
            {
                var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
                if (action != null && fpdf_doc.FPDFActionGetType(action) == PDFACTION_GOTO)
                    dest = fpdf_doc.FPDFActionGetDest(document, action);
            }
            return dest != null ? fpdf_doc.FPDFDestGetDestPageIndex(document, dest) : -1;
        }

        // ── Highlight annotations: load ───────────────────────────────

        /// <summary>Loads all highlight annotations from the document.</summary>
        public List<PdfHighlight> LoadHighlights(string filePath)
        {
            var result = new List<PdfHighlight>();
            var document = LoadDocument(filePath);
            try
            {
                int pageCount;
                lock (_pageLock) pageCount = fpdfview.FPDF_GetPageCount(document);
                for (int p = 0; p < pageCount; p++)
                    result.AddRange(LoadHighlightsFromPage(document, p));
            }
            finally
            {
                lock (_pageLock) fpdfview.FPDF_CloseDocument(document);
            }
            return result;
        }

        public List<PdfHighlight> LoadHighlights(string filePath, int pageNumber)
        {
            lock (_pageLock)
                return LoadHighlightsFromPage(GetOrLoadDocument(filePath), pageNumber);
        }

        private static List<PdfHighlight> LoadHighlightsFromPage(FpdfDocumentT document, int pageIndex)
        {
            var result = new List<PdfHighlight>();
            FpdfPageT page;
            lock (_pageLock)
            {
                page = fpdfview.FPDF_LoadPage(document, pageIndex);
            }
            if (page == null) return result;
            try
            {
                lock (_pageLock)
                {
                    int annotCount = fpdf_annot.FPDFPageGetAnnotCount(page);
                    for (int i = 0; i < annotCount; i++)
                    {
                        var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                        if (annot == null) continue;
                        try
                        {
                            if (fpdf_annot.FPDFAnnotGetSubtype(annot) != FPDF_ANNOT_HIGHLIGHT)
                                continue;

                            uint r = 0, g = 0, b = 0, a = 0;
                            if (fpdf_annot.FPDFAnnotGetColor(annot,
                                    FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                                    ref r, ref g, ref b, ref a) == 0)
                            {
                                // No /C entry — the producer baked the color into
                                // the /AP appearance stream (or omitted it entirely).
                                // Fall back to the de-facto standard highlighter
                                // yellow instead of the (0,0,0) the call leaves behind.
                                r = 255; g = 235; b = 59; a = 255;
                            }

                            var rects = new List<PdfRect>();
                            ulong quadCount = fpdf_annot.FPDFAnnotCountAttachmentPoints(annot);
                            for (ulong q = 0; q < quadCount; q++)
                            {
                                using var quad = new FS_QUADPOINTSF();
                                if (fpdf_annot.FPDFAnnotGetAttachmentPoints(annot, q, quad) != 0)
                                    rects.Add(QuadToRect(quad));
                            }

                            if (rects.Count > 0)
                            {
                                result.Add(new PdfHighlight(pageIndex, rects,
                                    Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b)));
                            }
                        }
                        finally
                        {
                            fpdf_annot.FPDFPageCloseAnnot(annot);
                        }
                    }
                }
            }
            finally
            {
                lock (_pageLock)
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
            return result;
        }

        private static PdfRect QuadToRect(FS_QUADPOINTSF quad)
        {
            // QuadPoints order (per PDF spec): top-left, top-right, bottom-left, bottom-right.
            double left = Math.Min(quad.X1, quad.X3);
            double right = Math.Max(quad.X2, quad.X4);
            double top = Math.Max(quad.Y1, quad.Y2);
            double bottom = Math.Min(quad.Y3, quad.Y4);
            return new PdfRect(left, top, right, bottom);
        }

        // ── Highlight annotations: save ───────────────────────────────

        /// <summary>
        /// Rewrites highlight annotations on <paramref name="modifiedPages"/> from
        /// <paramref name="highlights"/>. Highlights on every other page and all
        /// non-highlight annotations are preserved. Saves a full copy to
        /// <paramref name="tempPath"/>. The caller is responsible for atomically
        /// replacing the original file with the temp file. Thread-safe (guarded by _pageLock).
        /// </summary>
        public void SaveHighlightsToTemp(
            string filePath,
            IReadOnlyList<PdfHighlight> highlights,
            string tempPath,
            IReadOnlySet<int> modifiedPages)
        {
            var document = LoadDocument(filePath);
            try
            {
                var byPage = highlights
                    .GroupBy(h => h.PageIndex)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<PdfHighlight>)g.ToList());

                int pageCount = fpdfview.FPDF_GetPageCount(document);
                for (int p = 0; p < pageCount; p++)
                {
                    if (!modifiedPages.Contains(p)) continue;
                    byPage.TryGetValue(p, out var pageHighlights);

                    FpdfPageT page;
                    lock (_pageLock)
                    {
                        page = fpdfview.FPDF_LoadPage(document, p);
                    }
                    if (page == null) continue;
                    try
                    {
                        lock (_pageLock)
                        {
                            // Remove existing highlight annotations (iterate backwards).
                            for (int i = fpdf_annot.FPDFPageGetAnnotCount(page) - 1; i >= 0; i--)
                            {
                                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                                if (annot == null) continue;
                                int subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
                                fpdf_annot.FPDFPageCloseAnnot(annot);
                                if (subtype == FPDF_ANNOT_HIGHLIGHT)
                                    fpdf_annot.FPDFPageRemoveAnnot(page, i);
                            }

                            // Recreate from the in-memory model.
                            if (pageHighlights != null)
                            {
                                foreach (var highlight in pageHighlights)
                                    AppendHighlightAnnot(page, highlight);
                            }
                        }
                    }
                    finally
                    {
                        lock (_pageLock)
                        {
                            fpdfview.FPDF_ClosePage(page);
                        }
                    }
                }

                SaveDocumentAsCopy(document, tempPath);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        private static void AppendHighlightAnnot(FpdfPageT page, PdfHighlight highlight)
        {
            var annot = fpdf_annot.FPDFPageCreateAnnot(page, FPDF_ANNOT_HIGHLIGHT);
            if (annot == null) return;
            try
            {
                fpdf_annot.FPDFAnnotSetColor(annot,
                    FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                    highlight.Color.R, highlight.Color.G, highlight.Color.B,
                    highlight.Alpha);

                double left = highlight.Rects.Min(r => r.L);
                double right = highlight.Rects.Max(r => r.R);
                double top = highlight.Rects.Max(r => r.T);
                double bottom = highlight.Rects.Min(r => r.B);

                using var rect = new FS_RECTF_
                {
                    Left = (float)left,
                    Top = (float)top,
                    Right = (float)right,
                    Bottom = (float)bottom,
                };
                fpdf_annot.FPDFAnnotSetRect(annot, rect);

                foreach (var r in highlight.Rects)
                {
                    using var quad = new FS_QUADPOINTSF
                    {
                        X1 = (float)r.L, Y1 = (float)r.T, // top-left
                        X2 = (float)r.R, Y2 = (float)r.T, // top-right
                        X3 = (float)r.L, Y3 = (float)r.B, // bottom-left
                        X4 = (float)r.R, Y4 = (float)r.B, // bottom-right
                    };
                    fpdf_annot.FPDFAnnotAppendAttachmentPoints(annot, quad);
                }
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }

        private static void SaveDocumentAsCopy(FpdfDocumentT document, string tempPath)
        {
            using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var fileWrite = new FPDF_FILEWRITE_();

            // Same PDFiumCore lifetime issue as IFSDK_PAUSE: the setter only
            // stores a native function pointer, so root the delegate here and
            // keep it alive until FPDF_SaveAsCopy has finished calling it.
            Func_int___IntPtr___IntPtr_ulong writeBlock = (pThis, pData, size) =>
            {
                int length = (int)size;
                var bytes = new byte[length];
                Marshal.Copy(pData, bytes, 0, length);
                stream.Write(bytes, 0, length);
                return 1;
            };
            fileWrite.WriteBlock = writeBlock;

            int ok;
            lock (_pageLock)
            {
                ok = fpdf_save.FPDF_SaveAsCopy(document, fileWrite, 0);
            }
            GC.KeepAlive(writeBlock);
            GC.KeepAlive(fileWrite);
            if (ok == 0)
                throw new InvalidOperationException("FPDF_SaveAsCopy 保存失败");
        }

        // ════════════════════════════════════════════════════════════════
        //  IDisposable
        // ════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ReleaseAllTextPages();

            lock (_libraryLock)
            {
                _libraryRefCount--;
                if (_libraryRefCount == 0)
                    fpdfview.FPDF_DestroyLibrary();
            }
        }
    }
}
