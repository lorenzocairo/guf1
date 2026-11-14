using System.Globalization;
using System.IO;
using System.Text;

namespace Guf1;

/// <summary>
/// Persistenza delle registrazioni confermate: un CSV nella cartella dei risultati che contiene
/// solo le righe confermate. Viene creato alla prima conferma e riscritto a ogni modifica.
/// </summary>
public static class ConfirmedStore
{
    public const string FileName = "confermati.csv";

    private static readonly string[] Headers =
    [
        "Start (s)",
        "End (s)",
        "Scientific name",
        "Common name",
        "Confidence",
        "File",
        "Source CSV",
        "Recording time"
    ];

    public static string GetPath(string folderPath) => Path.Combine(folderPath, FileName);

    /// <summary>Indica se il nome file e' quello delle conferme (da escludere dai CSV BirdNET).</summary>
    public static bool IsConfirmedFile(string path) =>
        string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Chiavi delle registrazioni confermate; vuoto se il file non esiste.</summary>
    public static HashSet<string> Load(string folderPath)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var path = GetPath(folderPath);
        if (!File.Exists(path))
        {
            return keys;
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return keys;
        }

        var headers = MainWindow.ParseCsvLine(headerLine);
        var indexes = new[] { "Source CSV", "Start (s)", "End (s)", "Scientific name", "File" }
            .Select(name => headers.IndexOf(name))
            .ToArray();
        if (indexes.Any(index => index < 0))
        {
            throw new InvalidDataException("intestazioni non riconosciute");
        }

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = MainWindow.ParseCsvLine(line);
            if (indexes.Any(index => index >= fields.Count))
            {
                continue;
            }

            keys.Add(BirdNetResult.BuildConfirmationKey(
                fields[indexes[0]],
                fields[indexes[1]],
                fields[indexes[2]],
                fields[indexes[3]],
                fields[indexes[4]]));
        }

        return keys;
    }

    /// <summary>Riscrive il file con le sole righe confermate (scrittura su temporaneo, poi sostituzione).</summary>
    public static void Save(string folderPath, IEnumerable<BirdNetResult> confirmed)
    {
        var path = GetPath(folderPath);
        var tempPath = path + ".tmp";

        using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            writer.WriteLine(string.Join(',', Headers.Select(Escape)));
            foreach (var row in confirmed)
            {
                var recordingTime = row.FileStart is { } start
                    ? start.AddSeconds(row.StartValue).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : string.Empty;

                string[] fields =
                [
                    row.StartSeconds,
                    row.EndSeconds,
                    row.ScientificName,
                    row.CommonName,
                    row.Confidence,
                    row.AudioFile,
                    row.SourceCsv,
                    recordingTime
                ];
                writer.WriteLine(string.Join(',', fields.Select(Escape)));
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
