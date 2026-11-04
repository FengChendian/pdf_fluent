using System.Text.Json;
using pdf_fluent.Models;

namespace pdf_fluent.Services;

public sealed class RecentDocumentsService
{
    private readonly string _storagePath;

    public RecentDocumentsService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "pdf_fluent",
        "recent-documents.json"))
    {
    }

    internal RecentDocumentsService(string storagePath) => _storagePath = storagePath;

    public IReadOnlyList<RecentDocument> Load()
    {
        try
        {
            if (!File.Exists(_storagePath)) return [];
            var json = File.ReadAllText(_storagePath);
            return JsonSerializer.Deserialize<List<RecentDocument>>(json) ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<RecentDocument> documents)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            var json = JsonSerializer.Serialize(documents, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
