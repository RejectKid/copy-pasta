using System.Text.Json;
using System.IO;

namespace CopyPasta;

internal sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public const int MaxItems = 50;

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CopyPasta",
        "history.json");

    public IReadOnlyList<HistoryEntry> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions) ?? [];
            return entries
                .Where(entry => entry.HasContent)
                .OrderByDescending(entry => entry.CapturedAt)
                .Take(MaxItems)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<HistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var snapshot = entries.Take(MaxItems).ToList();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
