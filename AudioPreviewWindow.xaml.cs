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
    private readonly string _audioFilePath;
    private readonly double _startSeconds;
    private readonly double _endSeconds;
    private readonly double _durationSeconds;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer;
    private bool _isPlaying;
    private bool _isDraggingSlider;

    public AudioPreviewWindow(
        string audioFilePath,
        double startSeconds,
        double endSeconds,
        string commonName,
        string scientificName,
        string confidence)
    {
        InitializeComponent();

        _audioFilePath = audioFilePath;
        _startSeconds = startSeconds;
        _endSeconds = endSeconds;
        _durationSeconds = Math.Max(0, endSeconds - startSeconds);

        TitleText.Text = string.IsNullOrWhiteSpace(commonName)
            ? Path.GetFileName(audioFilePath)
            : $"{commonName} - {scientificName}";
        DetailsText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "Intervallo {0:0.###}s - {1:0.###}s ({2:0.###}s)  Confidenza {3}",
            startSeconds,
            endSeconds,
            _durationSeconds,
            confidence);
        PathText.Text = audioFilePath;
        PositionSlider.Maximum = _durationSeconds;
        UpdateTimeText(0);

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _timer.Tick += Timer_Tick;

        Loaded += AudioPreviewWindow_Loaded;
        OverlayCanvas.SizeChanged += (_, _) => UpdatePlayhead();
    }

    private async void AudioPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var image = await Task.Run(() => SpectrogramRenderer.Render(_audioFilePath, _startSeconds, _endSeconds));
            SpectrogramImage.Source = BitmapSource.Create(
                image.Width,
                image.Height,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                image.Pixels,
                image.Stride);
            LoadingText.Text = string.Empty;
            LoadingText.Visibility = Visibility.Collapsed;

            PlayPauseButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            ReplayButton.IsEnabled = true;
            PositionSlider.IsEnabled = true;

            _player.Open(new Uri(_audioFilePath, UriKind.Absolute));
            _player.MediaFailed += (_, args) => ShowMessage($"Impossibile riprodurre il file: {args.ErrorException.Message}");
            SeekRelative(0);
            UpdatePlayhead();
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
        PlayPauseButton.Content = "Pausa";
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
            PlayPauseButton.Content = "Pausa";
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
        PlayPauseButton.Content = "Play";
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

public sealed class SpectrogramData
{
    public SpectrogramData(int width, int height, byte[] pixels, int stride)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Stride = stride;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride { get; }
}
