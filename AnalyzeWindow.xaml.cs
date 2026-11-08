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

    private void MinConfSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (MinConfLabel is not null) MinConfLabel.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture); }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (SensitivityLabel is not null) SensitivityLabel.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture); }

    private void OverlapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (OverlapLabel is not null) OverlapLabel.Text = e.NewValue.ToString("0.0", CultureInfo.InvariantCulture); }

    private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (ThreadsLabel is not null) ThreadsLabel.Text = ((int)e.NewValue).ToString(); }

    private void BatchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (BatchLabel is not null) BatchLabel.Text = $"Batch: {(int)e.NewValue}"; }

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
            MinConfidence = MinConfSlider.Value,
            Sensitivity = SensitivitySlider.Value,
            Overlap = OverlapSlider.Value,
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
