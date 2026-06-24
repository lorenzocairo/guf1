using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Guf1;

public partial class AudioPreviewWindow : Window
{
    private readonly IReadOnlyList<RecordingInfo> _recordings;
    private int _currentIndex;
    private string _audioFilePath;
    private double _startSeconds;
    private double _endSeconds;
    private double _durationSeconds;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer;
    private bool _isPlaying;
    private bool _isDraggingSlider;

    public AudioPreviewWindow(
        IReadOnlyList<RecordingInfo> recordings,
        int currentIndex)
    {
        InitializeComponent();

        _recordings = recordings;
        _currentIndex = currentIndex;

        var rec = recordings[currentIndex];
        _audioFilePath = rec.AudioFilePath;
        _startSeconds = rec.StartSeconds;
        _endSeconds = rec.EndSeconds;
        _durationSeconds = Math.Max(0, rec.EndSeconds - rec.StartSeconds);

        ApplyRecordingUI(rec);
        UpdateNavigationButtons();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _timer.Tick += Timer_Tick;
        _player.MediaFailed += (_, args) => ShowMessage($"Impossibile riprodurre il file: {args.ErrorException.Message}");

        Loaded += AudioPreviewWindow_Loaded;
        OverlayCanvas.SizeChanged += (_, _) => UpdatePlayhead();
    }

    private void ApplyRecordingUI(RecordingInfo rec)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(rec.CommonName)
            ? Path.GetFileName(rec.AudioFilePath)
            : $"{rec.CommonName} - {rec.ScientificName}";
        DetailsText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "Intervallo {0:0.###}s - {1:0.###}s ({2:0.###}s)  Confidenza {3}",
            rec.StartSeconds,
            rec.EndSeconds,
            _durationSeconds,
            rec.Confidence);
        PathText.Text = rec.AudioFilePath;
        PositionSlider.Maximum = _durationSeconds;
        XAxisEndText.Text = string.Format(CultureInfo.CurrentCulture, "{0:0.00} s", _durationSeconds);
        UpdateTimeText(0);
    }

    private void UpdateNavigationButtons()
    {
        PrevButton.IsEnabled = _currentIndex > 0;
        NextButton.IsEnabled = _currentIndex < _recordings.Count - 1;
        NavIndexText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0} / {1}",
            _currentIndex + 1,
            _recordings.Count);
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex > 0)
        {
            NavigateTo(_currentIndex - 1);
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < _recordings.Count - 1)
        {
            NavigateTo(_currentIndex + 1);
        }
    }

    private async void NavigateTo(int index)
    {
        _timer.Stop();
        _isPlaying = false;
        _player.Close();
        PlayPauseButton.Content = "▶";

        _currentIndex = index;
        var rec = _recordings[index];
        _audioFilePath = rec.AudioFilePath;
        _startSeconds = rec.StartSeconds;
        _endSeconds = rec.EndSeconds;
        _durationSeconds = Math.Max(0, rec.EndSeconds - rec.StartSeconds);

        ApplyRecordingUI(rec);
        UpdateNavigationButtons();
        MessagePanel.Visibility = Visibility.Collapsed;

        try
        {
            _player.Open(new Uri(_audioFilePath, UriKind.Absolute));
            SeekRelative(0);
            UpdatePlayhead();
            await RenderSpectrogramAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            ShowMessage($"Impossibile caricare la registrazione: {ex.Message}");
        }
    }

    private async void AudioPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            PlayPauseButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            ReplayButton.IsEnabled = true;
            PositionSlider.IsEnabled = true;

            _player.Open(new Uri(_audioFilePath, UriKind.Absolute));
            SeekRelative(0);
            UpdatePlayhead();
            await RenderSpectrogramAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            ShowMessage($"Impossibile calcolare lo spettrogramma: {ex.Message}");
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Pause();
            return;
        }

        if (PositionSlider.Value >= _durationSeconds)
        {
            SeekRelative(0);
        }

        _player.Play();
        _timer.Start();
        _isPlaying = true;
        PlayPauseButton.Content = "\u23F8";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopAtStart();
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        SeekRelative(0);
        if (!_isPlaying)
        {
            _player.Play();
            _timer.Start();
            _isPlaying = true;
            PlayPauseButton.Content = "\u23F8";
        }
    }

    private void PositionSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
    }

    private void PositionSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        SeekRelative(PositionSlider.Value);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTimeText(e.NewValue);
        UpdatePlayhead();

        if (_isDraggingSlider)
        {
            return;
        }

        if (!_isPlaying)
        {
            SeekRelative(e.NewValue);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var relativeSeconds = _player.Position.TotalSeconds - _startSeconds;
        if (relativeSeconds >= _durationSeconds)
        {
            Pause();
            SeekRelative(_durationSeconds);
            return;
        }

        if (relativeSeconds < 0)
        {
            SeekRelative(0);
            return;
        }

        PositionSlider.Value = Math.Clamp(relativeSeconds, 0, _durationSeconds);
    }

    private void Pause()
    {
        _player.Pause();
        _timer.Stop();
        _isPlaying = false;
        PlayPauseButton.Content = "\u25B6";
    }

    private void StopAtStart()
    {
        Pause();
        SeekRelative(0);
    }

    private void SeekRelative(double relativeSeconds)
    {
        var clampedSeconds = Math.Clamp(relativeSeconds, 0, _durationSeconds);
        _player.Position = TimeSpan.FromSeconds(_startSeconds + clampedSeconds);
        PositionSlider.Value = clampedSeconds;
        UpdateTimeText(clampedSeconds);
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        if (_durationSeconds <= 0 || OverlayCanvas.ActualWidth <= 0 || OverlayCanvas.ActualHeight <= 0)
        {
            PlayheadLine.Visibility = Visibility.Collapsed;
            return;
        }

        var x = PositionSlider.Value / _durationSeconds * OverlayCanvas.ActualWidth;
        PlayheadLine.X1 = x;
        PlayheadLine.X2 = x;
        PlayheadLine.Y2 = OverlayCanvas.ActualHeight;
        PlayheadLine.Visibility = Visibility.Visible;
    }

    private void UpdateTimeText(double relativeSeconds)
    {
        TimeText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0:0.00}s / {1:0.00}s",
            Math.Clamp(relativeSeconds, 0, _durationSeconds),
            _durationSeconds);
    }

    private async Task RenderSpectrogramAsync()
    {
        LoadingText.Text = "Calcolo spettrogramma...";
        LoadingText.Visibility = Visibility.Visible;
        MessagePanel.Visibility = Visibility.Collapsed;

        try
        {
            var image = await Task.Run(() => SpectrogramRenderer.Render(_audioFilePath, _startSeconds, _endSeconds));

            SpectrogramImage.Source = BitmapSource.Create(
                image.Width,
                image.Height,
                300,
                96,
                PixelFormats.Bgr32,
                null,
                image.Pixels,
                image.Stride);
            YAxisTopText.Text = FormatFrequency(image.MaxFrequencyHertz);
            YAxisMiddleText.Text = FormatFrequency(GetMiddleMelFrequency(image.MinFrequencyHertz, image.MaxFrequencyHertz));
            LoadingText.Text = string.Empty;
            LoadingText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            ShowMessage($"Impossibile calcolare lo spettrogramma: {ex.Message}");
        }
    }

    private static string FormatFrequency(double hertz)
    {
        return hertz >= 1000
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} kHz", hertz / 1000)
            : string.Format(CultureInfo.CurrentCulture, "{0:0} Hz", hertz);
    }

    private static double GetMiddleMelFrequency(double minHertz, double maxHertz)
    {
        var minMel = 2595 * Math.Log10(1 + minHertz / 700);
        var maxMel = 2595 * Math.Log10(1 + maxHertz / 700);
        return 700 * (Math.Pow(10, ((minMel + maxMel) / 2) / 2595) - 1);
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _player.Close();
    }
}

public sealed record RecordingInfo(
    string AudioFilePath,
    double StartSeconds,
    double EndSeconds,
    string CommonName,
    string ScientificName,
    string Confidence);

public sealed class SpectrogramData
{
    public SpectrogramData(int width, int height, byte[] pixels, int stride, double minFrequencyHertz, double maxFrequencyHertz)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Stride = stride;
        MinFrequencyHertz = minFrequencyHertz;
        MaxFrequencyHertz = maxFrequencyHertz;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride { get; }
    public double MinFrequencyHertz { get; }
    public double MaxFrequencyHertz { get; }
}
