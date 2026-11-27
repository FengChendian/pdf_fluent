using System.Diagnostics;
using System.Text.Json;
using pdf_fluent.Services;

string path = Path.GetFullPath(args.Length > 0 ? args[0] : "tests/pdf_fluent.Tests/assets/CUDA_by_Example.pdf");
int pages;
using (var service = new PDFiumService()) pages = Math.Min(30, service.GetPageCount(path));
// Warm runtime/PDFium/JIT separately; each timed case uses a fresh service and
// preopens the document. Filesystem caching is intentionally warm in both cases.
using (var service = new PDFiumService()) { service.GetPageLinks(path, 2); service.GetPageText(path, 2); }
var rows = new List<object>();
foreach (string mode in new[] { "links_only", "text_then_links", "cached_links" })
{
    var times = new List<double>();
    var allocations = new List<long>();
    for (int run = 0; run < 5; run++)
    {
        using var service = new PDFiumService();
        service.GetPageCount(path);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int page = 0; page < pages; page++)
        {
            if (mode == "text_then_links") service.GetPageText(path, page);
            service.GetPageLinks(path, page);
        }
        if (mode == "cached_links")
        {
            // Measure cache hits on the most recently used page, not cold loads.
            before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            for (int i = 0; i < 10_000; i++) service.GetPageLinks(path, pages - 1);
        }
        watch.Stop();
        times.Add(watch.Elapsed.TotalMilliseconds);
        allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
    }
    rows.Add(new { mode, pages, calls = mode == "cached_links" ? 10_000 : pages,
        median_ms = times.Order().ElementAt(times.Count / 2),
        median_managed_bytes = allocations.Order().ElementAt(allocations.Count / 2) });
}
Console.WriteLine(JsonSerializer.Serialize(new { sample = Path.GetFileName(path), runs = 5, results = rows },
    new JsonSerializerOptions { WriteIndented = true }));
