using System.IO;
using System.Windows;

namespace Guf1;

public partial class SetupWizardWindow : Window
{
    private readonly PythonEnvironment _env;
    private CancellationTokenSource _cts = new();
    private bool _completed;

    public SetupWizardWindow(PythonEnvironment env)
    {
        InitializeComponent();
        _env = env;
    }

    // Called by App.xaml.cs after the window is shown
    public async Task RunSetupAsync()
    {
        var progress = new Progress<SetupStep>(step =>
        {
            StepLabel.Text = step.Message;
            if (step.ProgressPercent > 0)
                ProgressBar.Value = step.ProgressPercent;
            AppendLog(step.Message);
        });

        try
        {
            await _env.SetupAsync(progress, _cts.Token);
            _completed = true;
            StepLabel.Text = "Installazione completata.";
            ProgressBar.Value = 100;
            AppendLog("\nPronto. Clicca Continua per aprire GUF1.");
            ContinueButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Installazione annullata.");
            StepLabel.Text = "Annullata.";
        }
        catch (Exception ex)
        {
            AppendLog($"\nERRORE: {ex.Message}");
            StepLabel.Text = "Errore durante l'installazione.";
            System.Windows.MessageBox.Show(
                ex.Message,
                "Errore installazione",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + "\n");
        LogScroll.ScrollToEnd();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        System.Windows.Application.Current.Shutdown();
    }

    public bool SetupSucceeded { get; private set; }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SetupSucceeded = true;
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_completed)
        {
            _cts.Cancel();
        }
    }
}
