using System.IO;
using System.Text;

namespace Guf1;

/// <summary>Persistenza dei giorni "ascoltati": un CSV nella cartella dei risultati con un giorno per riga.</summary>
public static class ListenedStore
{
    public const string FileName = "ascoltati.csv";

    private const string Header = "Day";

    public static string GetPath(string folderPath) => Path.Combine(folderPath, FileName);

    public static bool IsListenedFile(string path) =>
        string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Chiavi (yyyy-MM-dd, o vuota se senza data) dei giorni ascoltati; vuoto se il file non esiste.</summary>
    public static HashSet<string> Load(string folderPath)
    {
        var days = new HashSet<string>(StringComparer.Ordinal);
        var path = GetPath(folderPath);
        if (!File.Exists(path))
        {
            return days;
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            return days;
        }

        if (MainWindow.ParseCsvLine(headerLine).FirstOrDefault() != Header)
        {
            throw new InvalidDataException("intestazioni non riconosciute");
        }

        while (reader.ReadLine() is { } line)
        {
            // Il gruppo "Data non riconosciuta" e' scritto come "" tra virgolette; le righe davvero vuote si ignorano.
            if (line.Length == 0)
            {
                continue;
            }

            days.Add(MainWindow.ParseCsvLine(line)[0]);
        }

        return days;
    }

    public static void Save(string folderPath, IEnumerable<string> listenedDays)
    {
        var path = GetPath(folderPath);
        var tempPath = path + ".tmp";

        using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            writer.WriteLine(Header);
            foreach (var day in listenedDays)
            {
                writer.WriteLine(day.Length == 0 ? "\"\"" : day);
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
