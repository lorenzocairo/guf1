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
using MediaImageSource = System.Windows.Media.ImageSource;
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

    private const double MinCardWidth = 330;

    // Tutti i risultati, gia' ordinati per data, start e CSV (stesso ordine delle card).
    private List<BirdNetResult> _allResults = [];
    private List<DayGroup> _days = [];
    private int _columnCount = 1;

    // Lista piatta mostrata dal ListBox: intestazioni giorno (DayGroup) e righe di card (CardRow).
    private ObservableCollection<object> _displayItems = [];

    // Riproduzione inline dalle card: un solo spezzone alla volta.
    private System.Windows.Media.MediaPlayer? _player;
    private System.Windows.Threading.DispatcherTimer? _playTimer;
    private BirdNetResult? _playing;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void AnalyzeAudio_Click(object sender, RoutedEventArgs e)
    {
        var win = new AnalyzeWindow { Owner = this };
        win.AnalysisCompleted += async folder => await LoadFolderAsync(folder);
        win.Show();
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
        _selectedFolderPath = folderPath;
        SelectedFolderText.Text = folderPath;
        HideMessage();
        SetLoading(isLoading: true);

        StopPlayback();

        // Svuota subito la lista: le card in uscita annullano i loro spettrogrammi.
        _allResults = [];
        _days = [];
        ResultsList.ItemsSource = null;

        FolderLoadResult folderResult;
        try
        {
            folderResult = await Task.Run(() => ReadFolder(folderPath));
        }
        finally
        {
            SetLoading(isLoading: false);
        }

        _allResults = folderResult.Rows.ToList();
        _days = _allResults
            .GroupBy(row => row.Day)
            .Select(group => new DayGroup(group.Key, group.ToList()))
            .ToList();
        RebuildDisplayItems();

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
            rows.AddRange(parseResult.Rows);
        }

        // Ordine: giorno, start, CSV. OrderBy e' stabile, quindi i giorni senza data restano insieme.
        var sortedRows = rows
            .OrderBy(row => row.SortDate)
            .ThenBy(row => row.FileStart)
            .ThenBy(row => row.StartValue)
            .ThenBy(row => row.SourceCsv, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var row in sortedRows)
        {
            if (!dayIndexes.TryGetValue(row.Day, out var dayIndex))
            {
                dayIndex = dayIndexes.Count;
                dayIndexes[row.Day] = dayIndex;
            }

            row.DayIndex = dayIndex;
            row.AudioPath = ResolveAudioFilePath(folderPath, row.AudioFile);
        }

        return new FolderLoadResult(csvFiles.Count, validFiles, invalidFiles, sortedRows);
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
            var sortDate = GetDayFromFileName(csvFile);
            var fileStart = ParseTimestamp(Path.GetFileName(csvFile));
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
                    SortDate = sortDate,
                    FileStart = fileStart ?? ParseTimestamp(Path.GetFileName(fields[5])),
                    StartValue = startSeconds,
                    EndValue = endSeconds,
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

    /// <summary>
    /// Ricava data e ora di inizio registrazione da nomi tipo "2MM26079_20260412_163008.BirdNET.results.csv"
    /// (AudioMoth): otto cifre di data seguite da sei cifre di ora, con separatore opzionale.
    /// </summary>
    private static DateTime? ParseTimestamp(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(?<!\d)(\d{8})[_\-T ]?(\d{6})(?!\d)");
        if (match.Success
            && DateTime.TryParseExact(
                match.Groups[1].Value + match.Groups[2].Value,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            return timestamp;
        }

        return null;
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
        RowsCountText.Text = _allResults.Count.ToString(CultureInfo.CurrentCulture);
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
        if (sender is not FrameworkElement { DataContext: BirdNetResult result })
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

    private void AudioPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BirdNetResult result })
        {
            OpenPreview(result);
        }
    }

    private void Spectrogram_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BirdNetResult result })
        {
            TogglePlayback(result);
        }
    }

    private void PlayCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BirdNetResult result })
        {
            TogglePlayback(result);
        }
    }

    private void EnsurePlayer()
    {
        if (_player is not null)
        {
            return;
        }

        _player = new System.Windows.Media.MediaPlayer();
        _player.MediaOpened += (_, _) =>
        {
            if (_playing is null)
            {
                return;
            }

            _player.Position = TimeSpan.FromSeconds(_playing.StartValue);
            _player.Play();
            _playTimer!.Start();
        };
        _player.MediaFailed += (_, args) =>
        {
            StopPlayback();
            ShowMessage($"Impossibile riprodurre il file: {args.ErrorException.Message}");
        };

        _playTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _playTimer.Tick += (_, _) =>
        {
            if (_playing is null)
            {
                return;
            }

            var duration = _playing.EndValue - _playing.StartValue;
            var elapsed = _player!.Position.TotalSeconds - _playing.StartValue;
            if (elapsed >= duration)
            {
                StopPlayback();
                return;
            }

            _playing.PlayProgress = duration > 0 ? Math.Max(0, elapsed) / duration : 0;
        };
    }

    /// <summary>Avvia lo spezzone della card (fermando quello in corso); se e' gia' in riproduzione lo ferma.</summary>
    private void TogglePlayback(BirdNetResult result)
    {
        if (ReferenceEquals(_playing, result))
        {
            StopPlayback();
            return;
        }

        StopPlayback();

        if (string.IsNullOrWhiteSpace(result.AudioPath))
        {
            ShowMessage("Il percorso del file audio non e disponibile.");
            return;
        }

        if (!File.Exists(result.AudioPath))
        {
            ShowMessage($"File non trovato: {result.AudioPath}");
            return;
        }

        if (result.EndValue <= result.StartValue)
        {
            ShowMessage("Intervallo audio non valido.");
            return;
        }

        try
        {
            EnsurePlayer();
            HideMessage();
            _playing = result;
            result.IsPlaying = true;
            _player!.Open(new Uri(result.AudioPath, UriKind.Absolute));
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException or InvalidOperationException)
        {
            StopPlayback();
            ShowMessage($"Impossibile riprodurre il file: {ex.Message}");
        }
    }

    private void StopPlayback()
    {
        _playTimer?.Stop();
        _player?.Close();
        if (_playing is not null)
        {
            _playing.IsPlaying = false;
            _playing = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        base.OnClosed(e);
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BirdNetResult result })
        {
            SpectrogramThumbnailLoader.Attach(result);
        }
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BirdNetResult result })
        {
            SpectrogramThumbnailLoader.Detach(result);
        }
    }

    private void DayHeader_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DayGroup group })
        {
            ToggleGroup(group);
        }
    }

    private void ResultsList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && ComputeColumnCount() != _columnCount)
        {
            RebuildDisplayItems(CaptureScrollAnchor());
        }
    }

    /// <summary>
    /// Apre o chiude un solo giorno inserendo/rimuovendo le sue righe nella lista esistente:
    /// tutto cio' che sta sopra resta invariato, quindi la posizione di scroll non si sposta.
    /// </summary>
    private void ToggleGroup(DayGroup group)
    {
        var headerIndex = _displayItems.IndexOf(group);
        if (headerIndex < 0)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;

        if (group.IsExpanded)
        {
            var insertAt = headerIndex + 1;
            foreach (var row in BuildRows(group))
            {
                _displayItems.Insert(insertAt++, row);
            }

            return;
        }

        while (headerIndex + 1 < _displayItems.Count && _displayItems[headerIndex + 1] is CardRow)
        {
            _displayItems.RemoveAt(headerIndex + 1);
        }

        // Se l'utente stava guardando le righe appena rimosse, riporta in vista l'intestazione.
        ResultsList.ScrollIntoView(group);
    }

    private List<CardRow> BuildRows(DayGroup group)
    {
        var rows = new List<CardRow>((group.Items.Count + _columnCount - 1) / _columnCount);
        for (var i = 0; i < group.Items.Count; i += _columnCount)
        {
            var cards = group.Items.GetRange(i, Math.Min(_columnCount, group.Items.Count - i)).ToArray();
            rows.Add(new CardRow(cards, _columnCount, group));
        }

        return rows;
    }

    /// <summary>Elemento (giorno o prima card di una riga) mostrato in cima alla lista in questo momento.</summary>
    private object? CaptureScrollAnchor()
    {
        return GetTopItem() switch
        {
            DayGroup group => group,
            CardRow row => row.Cards[0],
            _ => null
        };
    }

    private void ResultsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateStickyHeader();
    }

    /// <summary>
    /// Mostra in alto il giorno a cui appartiene l'elemento in cima alla vista. Resta nascosto
    /// quando l'intestazione reale e' gia' in cima (offset 0), per non duplicarla.
    /// </summary>
    private void UpdateStickyHeader()
    {
        DayGroup? group = null;
        var showOverlay = false;

        switch (GetTopItem())
        {
            case CardRow row:
                group = row.Group;
                showOverlay = true;
                break;
            case DayGroup header:
                group = header;
                showOverlay = FindChild<ScrollViewer>(ResultsList)?.VerticalOffset > 0;
                break;
        }

        if (!showOverlay || group is null)
        {
            StickyHeaderHost.Visibility = Visibility.Collapsed;
            StickyHeader.Content = null;
            return;
        }

        if (!ReferenceEquals(StickyHeader.Content, group))
        {
            StickyHeader.Content = group;
        }

        StickyHeaderHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Elemento che si trova sul bordo superiore della vista, calcolato dalla posizione reale.
    /// Non basta guardare il primo contenitore del pannello: WPF rimuove quelli gia' superati con
    /// una pulizia differita, quindi per un po' restano in lista anche se sono fuori schermo.
    /// </summary>
    private object? GetTopItem()
    {
        var scrollViewer = FindChild<ScrollViewer>(ResultsList);
        var panel = FindChild<VirtualizingStackPanel>(ResultsList);
        if (scrollViewer is null || panel is null)
        {
            return null;
        }

        double viewportTop;
        try
        {
            viewportTop = scrollViewer.TransformToAncestor(ResultsList).Transform(new System.Windows.Point(0, 0)).Y;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        object? top = null;
        var topY = double.NegativeInfinity;
        object? firstBelow = null;
        var firstBelowY = double.PositiveInfinity;

        foreach (var child in panel.Children)
        {
            if (child is not ListBoxItem { Content: { } content } container)
            {
                continue;
            }

            double y;
            try
            {
                y = container.TransformToAncestor(ResultsList).Transform(new System.Windows.Point(0, 0)).Y;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (y <= viewportTop + 1)
            {
                // Tra quelli che iniziano sopra il bordo, vale l'ultimo (il piu' vicino al bordo).
                if (y > topY)
                {
                    topY = y;
                    top = content;
                }
            }
            else if (y < firstBelowY)
            {
                firstBelowY = y;
                firstBelow = content;
            }
        }

        return top ?? firstBelow;
    }

    private int ComputeColumnCount()
    {
        // 30 px circa per scrollbar e bordi.
        return Math.Max(1, (int)((ResultsList.ActualWidth - 30) / MinCardWidth));
    }

    /// <summary>
    /// Ricostruisce la lista piatta (intestazioni giorno + righe di card). La virtualizzazione del
    /// ListBox crea i contenitori, e quindi gli spettrogrammi, solo per cio' che e' a schermo.
    /// </summary>
    private void RebuildDisplayItems(object? anchor = null)
    {
        _columnCount = ComputeColumnCount();
        var items = new List<object>();
        object? target = null;
        foreach (var group in _days)
        {
            items.Add(group);
            if (ReferenceEquals(group, anchor))
            {
                target = group;
            }

            if (!group.IsExpanded)
            {
                continue;
            }

            foreach (var row in BuildRows(group))
            {
                items.Add(row);
                if (anchor is BirdNetResult card && row.Cards.Contains(card))
                {
                    target = row;
                }
            }
        }

        // Anchor non piu' visibile (giorno compresso): ripiega sull'intestazione del suo giorno.
        if (target is null && anchor is BirdNetResult hiddenCard)
        {
            target = _days.FirstOrDefault(group => group.Items.Contains(hiddenCard));
        }

        _displayItems = new ObservableCollection<object>(items);
        ResultsList.ItemsSource = _displayItems;

        if (target is not null && items.Count > 0)
        {
            // Portare prima in vista l'ultimo elemento e poi il target lo posiziona in cima alla lista.
            ResultsList.ScrollIntoView(items[^1]);
            ResultsList.UpdateLayout();
            ResultsList.ScrollIntoView(target);
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var found = FindChild<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void OpenPreview(BirdNetResult result)
    {
        StopPlayback();

        try
        {
            var audioFilePath = ResolveAudioFilePath(result.AudioFile);
            if (string.IsNullOrWhiteSpace(audioFilePath))
            {
                ShowMessage("Il percorso del file audio non e disponibile.");
                return;
            }

            if (!File.Exists(audioFilePath))
            {
                ShowMessage($"File non trovato: {audioFilePath}");
                return;
            }

            if (result.EndValue <= result.StartValue)
            {
                ShowMessage("Intervallo audio non valido.");
                return;
            }

            var recordings = new List<RecordingInfo>();
            var currentIndex = 0;
            var i = 0;
            foreach (var item in _allResults)
            {
                recordings.Add(new RecordingInfo(
                    item.AudioPath,
                    item.StartValue,
                    item.EndValue,
                    item.CommonName,
                    item.ScientificName,
                    item.Confidence));
                if (ReferenceEquals(item, result))
                {
                    currentIndex = i;
                }
                i++;
            }

            var preview = new AudioPreviewWindow(recordings, currentIndex)
            {
                Owner = this
            };

            preview.ShowDialog();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ShowMessage($"Impossibile aprire l'anteprima audio: {ex.Message}");
        }
    }

    private string ResolveAudioFilePath(string audioFile) => ResolveAudioFilePath(_selectedFolderPath, audioFile);

    private static string ResolveAudioFilePath(string folderPath, string audioFile)
    {
        if (string.IsNullOrWhiteSpace(audioFile))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(audioFile)
            ? audioFile
            : Path.GetFullPath(Path.Combine(folderPath, audioFile));
    }

    private void ExpandOrCollapseAllGroups(bool isExpanded)
    {
        var anchor = CaptureScrollAnchor();
        foreach (var group in _days)
        {
            group.IsExpanded = isExpanded;
        }

        RebuildDisplayItems(anchor);
    }

    private void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.IsEnabled = !isLoading;
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

public sealed class DayGroup : INotifyPropertyChanged
{
    public DayGroup(string name, List<BirdNetResult> items)
    {
        Name = name;
        Items = items;
    }

    public string Name { get; }
    public List<BirdNetResult> Items { get; }
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isExpanded = true;
    public int Count => Items.Count;
    public string Title => $"{(IsExpanded ? "▾" : "▸")}  {Name}";
}

public sealed class CardRow
{
    public CardRow(BirdNetResult[] cards, int columnCount, DayGroup group)
    {
        Cards = cards;
        ColumnCount = columnCount;
        Group = group;
    }

    public DayGroup Group { get; }
    public BirdNetResult[] Cards { get; }
    public int ColumnCount { get; }
}

public sealed class BirdNetResult : INotifyPropertyChanged
{
    private static readonly MediaBrush EvenDayBackground = new SolidMediaBrush(MediaColor.FromRgb(247, 251, 255));
    private static readonly MediaBrush OddDayBackground = new SolidMediaBrush(MediaColor.FromRgb(255, 251, 242));

    private MediaImageSource? _spectrogram;
    private string _spectrogramStatus = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isPlaying;
    private double _playProgress;

    public string AudioPath { get; set; } = string.Empty;
    public string TimeRange => $"{StartSeconds} - {EndSeconds} s";

    /// <summary>Data e ora di inizio del file registrato, dedotte dal nome file.</summary>
    public DateTime? FileStart { get; init; }

    /// <summary>Ora (orologio) del rilevamento: inizio file + offset dello spezzone.</summary>
    public string RecordingTime => FileStart is { } start
        ? start.AddSeconds(StartValue).ToString("HH:mm:ss", CultureInfo.CurrentCulture)
        : "Ora non disponibile";

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value)
            {
                return;
            }

            _isPlaying = value;
            PlayProgress = 0;
            Notify(nameof(IsPlaying));
            Notify(nameof(PlayGlyph));
        }
    }

    public string PlayGlyph => _isPlaying ? "■" : "▶";

    /// <summary>Avanzamento della riproduzione, da 0 a 1.</summary>
    public double PlayProgress
    {
        get => _playProgress;
        set
        {
            _playProgress = Math.Clamp(value, 0, 1);
            Notify(nameof(PlayheadBefore));
            Notify(nameof(PlayheadAfter));
        }
    }

    // Colonne proporzionali per posizionare il cursore di riproduzione sopra lo spettrogramma.
    public GridLength PlayheadBefore => new(_playProgress, GridUnitType.Star);
    public GridLength PlayheadAfter => new(1 - _playProgress, GridUnitType.Star);

    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // Stato del caricamento spettrogramma, gestito da SpectrogramThumbnailLoader (thread UI).
    public int ViewCount { get; set; }
    public CancellationTokenSource? LoadCts { get; set; }
    public MediaImageSource? Spectrogram => _spectrogram;
    public string SpectrogramStatus => _spectrogramStatus;

    public void SetSpectrogram(MediaImageSource image) => SetSpectrogramState(image, string.Empty);

    public void SetSpectrogramStatus(string status) => SetSpectrogramState(null, status);

    public void SetSpectrogramError(string message) => SetSpectrogramState(null, $"Spettrogramma non disponibile: {message}");

    public void ClearSpectrogram() => SetSpectrogramState(null, string.Empty);

    private void SetSpectrogramState(MediaImageSource? image, string status)
    {
        _spectrogram = image;
        _spectrogramStatus = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Spectrogram)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpectrogramStatus)));
    }

    public DateTime? SortDate { get; init; }
    public double StartValue { get; init; }
    public double EndValue { get; init; }
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
