using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using SolidMediaBrush = System.Windows.Media.SolidColorBrush;

namespace Guf1;

public partial class MainWindow : Window
{
    private string _selectedFolderPath = string.Empty;

    private static readonly string[] ExpectedHeaders =
    [
        "Start (s)",
        "End (s)",
        "Scientific name",
        "Common name",
        "Confidence",
        "File"
    ];

    public ObservableCollection<BirdNetResult> Results { get; } = [];
    public bool AreGroupsExpanded
    {
        get => (bool)GetValue(AreGroupsExpandedProperty);
        set => SetValue(AreGroupsExpandedProperty, value);
    }

    public static readonly DependencyProperty AreGroupsExpandedProperty =
        DependencyProperty.Register(
            nameof(AreGroupsExpanded),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(true));

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var view = CollectionViewSource.GetDefaultView(Results);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BirdNetResult.Day)));
        view.SortDescriptions.Add(new SortDescription(nameof(BirdNetResult.SortDate), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(BirdNetResult.SourceCsv), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(BirdNetResult.StartValue), ListSortDirection.Ascending));
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Seleziona la cartella che contiene i CSV BirdNET",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        await LoadFolderAsync(dialog.SelectedPath);
    }

    private async Task LoadFolderAsync(string folderPath)
    {
        Results.Clear();
        _selectedFolderPath = folderPath;
        SelectedFolderText.Text = folderPath;
        HideMessage();
        SetLoading(isLoading: true);

        FolderLoadResult folderResult;
        try
        {
            folderResult = await Task.Run(() => ReadFolder(folderPath));
        }
        finally
        {
            SetLoading(isLoading: false);
        }

        foreach (var row in folderResult.Rows)
        {
            Results.Add(row);
        }

        UpdateSummary(folderResult.ValidFiles, folderResult.InvalidFiles.Count);

        if (folderResult.CsvFilesCount == 0)
        {
            ShowMessage("La cartella selezionata non contiene file CSV.");
            return;
        }

        if (folderResult.ValidFiles == 0)
        {
            ShowMessage("Nessun CSV nella cartella rispetta il formato BirdNET atteso.");
            return;
        }

        if (folderResult.InvalidFiles.Count > 0)
        {
            ShowMessage("Alcuni file sono stati scartati:\n" + string.Join("\n", folderResult.InvalidFiles));
            return;
        }

        HideMessage();
        ExpandOrCollapseAllGroups(isExpanded: true);
    }

    private static FolderLoadResult ReadFolder(string folderPath)
    {
        var csvFiles = Directory
            .EnumerateFiles(folderPath, "*.csv", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (csvFiles.Count == 0)
        {
            return new FolderLoadResult(0, 0, [], []);
        }

        var validFiles = 0;
        var invalidFiles = new List<string>();
        var dayIndexes = new Dictionary<string, int>();
        var rows = new List<BirdNetResult>();

        foreach (var csvFile in csvFiles)
        {
            var parseResult = TryReadBirdNetCsv(csvFile);
            if (!parseResult.IsValid)
            {
                invalidFiles.Add($"{Path.GetFileName(csvFile)}: {parseResult.Error}");
                continue;
            }

            validFiles++;
            foreach (var row in parseResult.Rows)
            {
                if (!dayIndexes.TryGetValue(row.Day, out var dayIndex))
                {
                    dayIndex = dayIndexes.Count;
                    dayIndexes[row.Day] = dayIndex;
                }

                row.DayIndex = dayIndex;
                rows.Add(row);
            }
        }

        return new FolderLoadResult(csvFiles.Count, validFiles, invalidFiles, rows);
    }

    private static CsvParseResult TryReadBirdNetCsv(string csvFile)
    {
        try
        {
            using var reader = new StreamReader(csvFile);
            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return CsvParseResult.Invalid("file vuoto");
            }

            var headers = ParseCsvLine(headerLine);
            if (!headers.SequenceEqual(ExpectedHeaders, StringComparer.Ordinal))
            {
                return CsvParseResult.Invalid("intestazioni non riconosciute");
            }

            var rows = new List<BirdNetResult>();
            var lineNumber = 1;
            while (!reader.EndOfStream)
            {
                lineNumber++;
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);
                if (fields.Count != ExpectedHeaders.Length)
                {
                    return CsvParseResult.Invalid($"riga {lineNumber} con {fields.Count} colonne invece di {ExpectedHeaders.Length}");
                }

                if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var startSeconds)
                    || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var endSeconds)
                    || !double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
                {
                    return CsvParseResult.Invalid($"riga {lineNumber} con valori numerici non validi");
                }

                rows.Add(new BirdNetResult
                {
                    SortDate = GetDayFromFileName(csvFile),
                    StartValue = startSeconds,
                    SourceCsv = Path.GetFileName(csvFile),
                    StartSeconds = startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    EndSeconds = endSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    ScientificName = fields[2],
                    CommonName = fields[3],
                    Confidence = confidence.ToString("0.0000", CultureInfo.InvariantCulture),
                    AudioFile = fields[5]
                });
            }

            return CsvParseResult.Valid(rows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CsvParseResult.Invalid(ex.Message);
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static DateTime? GetDayFromFileName(string csvFile)
    {
        var fileName = Path.GetFileName(csvFile);
        var parts = fileName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length == 8
                && DateTime.TryParseExact(part, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        return null;
    }

    private void UpdateSummary(int validFiles, int invalidFiles)
    {
        RowsCountText.Text = Results.Count.ToString(CultureInfo.CurrentCulture);
        ValidFilesText.Text = validFiles.ToString(CultureInfo.CurrentCulture);
        InvalidFilesText.Text = invalidFiles.ToString(CultureInfo.CurrentCulture);
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageText.Text = string.Empty;
        MessagePanel.Visibility = Visibility.Collapsed;
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        ExpandOrCollapseAllGroups(isExpanded: true);
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        ExpandOrCollapseAllGroups(isExpanded: false);
    }

    private void AudioFileLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { DataContext: BirdNetResult result })
        {
            return;
        }

        try
        {
            var audioFilePath = ResolveAudioFilePath(result.AudioFile);
            if (string.IsNullOrWhiteSpace(audioFilePath))
            {
                ShowMessage("Il percorso del file audio non e disponibile.");
                return;
            }

            if (File.Exists(audioFilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{audioFilePath}\"",
                    UseShellExecute = true
                });
                return;
            }

            if (Directory.Exists(audioFilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = audioFilePath,
                    UseShellExecute = true
                });
                return;
            }

            ShowMessage($"File non trovato: {audioFilePath}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            ShowMessage($"Impossibile aprire Esplora file: {ex.Message}");
        }
    }

    private string ResolveAudioFilePath(string audioFile)
    {
        if (string.IsNullOrWhiteSpace(audioFile))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(audioFile)
            ? audioFile
            : Path.GetFullPath(Path.Combine(_selectedFolderPath, audioFile));
    }

    private void ExpandOrCollapseAllGroups(bool isExpanded)
    {
        if (AreGroupsExpanded == isExpanded)
        {
            AreGroupsExpanded = !isExpanded;
        }

        AreGroupsExpanded = isExpanded;
        CollectionViewSource.GetDefaultView(Results).Refresh();
        ResultsGrid.UpdateLayout();
    }

    private void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ResultsGrid.IsEnabled = !isLoading;
        ExpandAllButton.IsEnabled = !isLoading;
        CollapseAllButton.IsEnabled = !isLoading;
    }
}

public sealed class FolderLoadResult
{
    public FolderLoadResult(
        int csvFilesCount,
        int validFiles,
        IReadOnlyList<string> invalidFiles,
        IReadOnlyList<BirdNetResult> rows)
    {
        CsvFilesCount = csvFilesCount;
        ValidFiles = validFiles;
        InvalidFiles = invalidFiles;
        Rows = rows;
    }

    public int CsvFilesCount { get; }
    public int ValidFiles { get; }
    public IReadOnlyList<string> InvalidFiles { get; }
    public IReadOnlyList<BirdNetResult> Rows { get; }
}

public sealed class BirdNetResult
{
    private static readonly MediaBrush EvenDayBackground = new SolidMediaBrush(MediaColor.FromRgb(247, 251, 255));
    private static readonly MediaBrush OddDayBackground = new SolidMediaBrush(MediaColor.FromRgb(255, 251, 242));

    public DateTime? SortDate { get; init; }
    public double StartValue { get; init; }
    public int DayIndex { get; set; }
    public string Day => SortDate?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) ?? "Data non riconosciuta";
    public MediaBrush DayBackground => DayIndex % 2 == 0 ? EvenDayBackground : OddDayBackground;
    public string SourceCsv { get; init; } = string.Empty;
    public string StartSeconds { get; init; } = string.Empty;
    public string EndSeconds { get; init; } = string.Empty;
    public string ScientificName { get; init; } = string.Empty;
    public string CommonName { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public string AudioFile { get; init; } = string.Empty;
}

public sealed class CsvParseResult
{
    private CsvParseResult(bool isValid, IReadOnlyList<BirdNetResult> rows, string error)
    {
        IsValid = isValid;
        Rows = rows;
        Error = error;
    }

    public bool IsValid { get; }
    public IReadOnlyList<BirdNetResult> Rows { get; }
    public string Error { get; }

    public static CsvParseResult Valid(IReadOnlyList<BirdNetResult> rows) => new(true, rows, string.Empty);

    public static CsvParseResult Invalid(string error) => new(false, [], error);
}
