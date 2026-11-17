using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Guf1;

public partial class AnalyzeWindow : Window
{
    private readonly PythonEnvironment _env;
    private readonly BirdNetRunner _runner;
    private CancellationTokenSource? _cts;

    // Raised when analysis completes so MainWindow can reload results
    public event Action<string>? AnalysisCompleted;

    public AnalyzeWindow()
    {
        InitializeComponent();

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Guf1", "BirdNET");

        _env = new PythonEnvironment(appData);
        _runner = new BirdNetRunner(_env);

        // Thread limitati ai core logici disponibili, batch a un massimo prudente
        var cores = Math.Max(1, Environment.ProcessorCount);
        ThreadsSlider.Maximum = cores;
        ThreadsSlider.Value = Math.Max(1, cores / 2);
        BatchSlider.Maximum = 8;
        UpdatePerfHint();

        PopulateWeekCombo();
        RefreshEnvStatus();
    }

    private void PopulateWeekCombo()
    {
        WeekCombo.Items.Add(new ComboBoxItem { Content = "Auto (-1)", Tag = -1 });
        for (var w = 1; w <= 48; w++)
        {
            WeekCombo.Items.Add(new ComboBoxItem { Content = w.ToString(), Tag = w });
        }
        WeekCombo.SelectedIndex = 0;
    }

    private void RefreshEnvStatus()
    {
        if (_env.IsReady)
        {
            EnvStatusText.Text = "Ambiente pronto";
            EnvStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x7A, 0x3C));
            UpdateRunButton();
        }
        else
        {
            EnvStatusText.Text = "Ambiente non configurato";
            EnvStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xB4, 0x5A, 0x0A));
            RunButton.IsEnabled = false;
        }
    }

    private void UpdateRunButton()
    {
        RunButton.IsEnabled = _env.IsReady
            && !string.IsNullOrWhiteSpace(InputFolderBox.Text)
            && !string.IsNullOrWhiteSpace(OutputFolderBox.Text);
    }

    // --- Path pickers ---

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var folder = PickFolder("Seleziona la cartella con i file audio");
        if (folder is null) return;
        InputFolderBox.Text = folder;
        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
        {
            OutputFolderBox.Text = Path.Combine(folder, "birdnet_output");
        }
        UpdateRunButton();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var folder = PickFolder("Seleziona la cartella di output per i CSV");
        if (folder is null) return;
        OutputFolderBox.Text = folder;
        UpdateRunButton();
    }

    private static string? PickFolder(string description)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    // --- Slider labels ---

    private static bool TryParseInRange(string name, string text, double min, double max, out double value)
    {
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || value < min || value > max)
        {
            System.Windows.MessageBox.Show(
                $"Il valore di \"{name}\" non è valido. Inserisci un numero tra {min.ToString(CultureInfo.InvariantCulture)} e {max.ToString(CultureInfo.InvariantCulture)}.",
                "Valore non valido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (ThreadsLabel is not null) ThreadsLabel.Text = ((int)e.NewValue).ToString(); UpdatePerfHint(); }

    private void BatchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdatePerfHint();

    private void UpdatePerfHint()
    {
        if (BatchLabel is null || ThreadsSlider is null || BatchSlider is null) return;
        BatchLabel.Text = $"Thread max = core disponibili ({Environment.ProcessorCount}), batch max {(int)BatchSlider.Maximum}. " +
                          $"Valori alti usano molta RAM: se compare un errore, abbassali. Attuale: {(int)ThreadsSlider.Value} thread, batch {(int)BatchSlider.Value}.";
    }

    private void LatBox_TextChanged(object sender, TextChangedEventArgs e) { }
    private void LonBox_TextChanged(object sender, TextChangedEventArgs e) { }

    private void BrowseSpeciesList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Seleziona lista specie BirdNET",
            Filter = "File testo (*.txt)|*.txt|Tutti i file (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SpeciesListBox.Text = dlg.FileName;
    }

    private void ClearSpeciesList_Click(object sender, RoutedEventArgs e)
        => SpeciesListBox.Text = string.Empty;

    // --- Run ---

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions();
        if (options is null) return;

        SetUiRunning(true);
        LogBox.Clear();
        ProgressBar.IsIndeterminate = true;

        var outputProgress = new Progress<string>(AppendLog);

        try
        {
            _cts = new CancellationTokenSource();
            await _runner.RunAsync(options, outputProgress, _cts.Token);
            AppendLog("\nAnalisi completata.");
            AnalysisCompleted?.Invoke(options.OutputFolder);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Analisi annullata.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERRORE: {ex.Message}");
            System.Windows.MessageBox.Show(ex.Message, "Errore analisi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgressBar.IsIndeterminate = false;
            SetUiRunning(false);
        }
    }

    private BirdNetOptions? BuildOptions()
    {
        if (string.IsNullOrWhiteSpace(InputFolderBox.Text))
        {
            System.Windows.MessageBox.Show("Seleziona la cartella audio di input.", "Parametro mancante", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
        {
            System.Windows.MessageBox.Show("Seleziona la cartella di output.", "Parametro mancante", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (!TryParseInRange("Confidenza minima", MinConfBox.Text, 0.01, 0.99, out var minConf)) { MinConfBox.Focus(); return null; }
        if (!TryParseInRange("Sensitivity", SensitivityBox.Text, 0.5, 1.5, out var sensitivity)) { SensitivityBox.Focus(); return null; }
        if (!TryParseInRange("Overlap (s)", OverlapBox.Text, 0.0, 2.9, out var overlap)) { OverlapBox.Focus(); return null; }

        var lat = -1.0;
        var lon = -1.0;

        if (!string.IsNullOrWhiteSpace(LatBox.Text))
            double.TryParse(LatBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
        if (!string.IsNullOrWhiteSpace(LonBox.Text))
            double.TryParse(LonBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out lon);

        var week = -1;
        if (WeekCombo.SelectedItem is ComboBoxItem { Tag: int w })
            week = w;

        var locale = "en";
        if (LocaleCombo.SelectedItem is ComboBoxItem { Tag: string loc })
            locale = loc;

        return new BirdNetOptions
        {
            InputFolder = InputFolderBox.Text,
            OutputFolder = OutputFolderBox.Text,
            Latitude = lat,
            Longitude = lon,
            Week = week,
            MinConfidence = minConf,
            Sensitivity = sensitivity,
            Overlap = overlap,
            Threads = (int)ThreadsSlider.Value,
            BatchSize = (int)BatchSlider.Value,
            Locale = locale,
            CustomSpeciesList = SpeciesListBox.Text.Trim(),
        };
    }

    private void SetUiRunning(bool running)
    {
        RunButton.IsEnabled = !running && _env.IsReady
            && !string.IsNullOrWhiteSpace(InputFolderBox.Text)
            && !string.IsNullOrWhiteSpace(OutputFolderBox.Text);
        ProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + "\n");
        LogScroll.ScrollToEnd();
    }
}
