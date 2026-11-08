using System.IO;
using System.Windows;

namespace Guf1;

public partial class App : System.Windows.Application
{
    private async void App_Startup(object sender, StartupEventArgs e)
    {
        var envDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Guf1", "BirdNET");

        var env = new PythonEnvironment(envDir);

        if (!env.IsReady)
        {
            var wizard = new SetupWizardWindow(env);
            wizard.Show();

            // RunSetupAsync aggiorna la UI della wizard mentre lavora
            await wizard.RunSetupAsync();

            // Se l'utente ha annullato o chiuso, non aprire MainWindow
            if (!wizard.SetupSucceeded)
                return;
        }

        var main = new MainWindow();
        main.Show();
    }
}
