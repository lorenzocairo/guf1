using System.IO;
using System.Text.Json;

namespace Guf1;

/// <summary>Elenco persistente delle ultime cartelle di risultati aperte (la piu' recente per prima).</summary>
public static class RecentFoldersStore
{
    public const int MaxItems = 10;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Guf1", "recent-folders.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
            return items?.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxItems).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static List<string> Add(string folder)
    {
        var items = Load();
        items.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        items.Insert(0, folder);
        if (items.Count > MaxItems)
        {
            items.RemoveRange(MaxItems, items.Count - MaxItems);
        }

        Save(items);
        return items;
    }

    public static List<string> Remove(string folder)
    {
        var items = Load();
        items.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        Save(items);
        return items;
    }

    public static void Clear() => Save([]);

    private static void Save(List<string> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(items));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Lista recenti non critica: se non si riesce a salvare si prosegue.
        }
    }
}
