using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace Guf1;

public sealed class PythonEnvironment
{
    // Python 3.11.9 embeddable — richiesto da birdnet-analyzer >= 2.0
    private const string PythonVersion = "3.11.9";
    private const string PythonZipUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip";
    private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";
    private const string BirdNetPackage = "birdnet-analyzer";

    private readonly string _baseDir;
    public string PythonExe { get; }

    // Cartella del package installato via pip --target
    private string BirdNetPackageDir =>
        Path.Combine(_baseDir, "python", "Lib", "site-packages", "birdnet_analyzer");

    public PythonEnvironment(string baseDir)
    {
        _baseDir = baseDir;
        PythonExe = Path.Combine(baseDir, "python", "python.exe");
    }

    public bool IsReady =>
        File.Exists(PythonExe) && Directory.Exists(BirdNetPackageDir);

    public async Task SetupAsync(IProgress<SetupStep> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_baseDir);
        var pythonDir = Path.Combine(_baseDir, "python");

        if (!File.Exists(PythonExe))
        {
            await DownloadAndExtractPythonAsync(pythonDir, progress, ct);
            EnableSitePackages(pythonDir);
            await InstallPipAsync(pythonDir, progress, ct);
        }

        if (!Directory.Exists(BirdNetPackageDir))
        {
            await InstallBirdNetAsync(pythonDir, progress, ct);
        }
    }

    private static async Task DownloadAndExtractPythonAsync(string pythonDir, IProgress<SetupStep> progress, CancellationToken ct)
    {
        progress.Report(new SetupStep("Download Python embeddable…", 0));

        Directory.CreateDirectory(pythonDir);
        var zipPath = Path.Combine(pythonDir, "python-embed.zip");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var response = await http.GetAsync(PythonZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(zipPath);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                progress.Report(new SetupStep("Download Python embeddable…", (int)(downloaded * 40 / total)));
            }
        }

        file.Close();

        progress.Report(new SetupStep("Estrazione Python…", 40));
        ZipFile.ExtractToDirectory(zipPath, pythonDir, overwriteFiles: true);
        File.Delete(zipPath);
    }

    private static void EnableSitePackages(string pythonDir)
    {
        // The embeddable zip ships with a ._pth file that has site disabled.
        // We must uncomment the "import site" line so pip and installed packages work.
        var pthFile = Directory
            .EnumerateFiles(pythonDir, "python*._pth")
            .FirstOrDefault();

        if (pthFile is null) return;

        var lines = File.ReadAllLines(pthFile).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("#import site", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "import site";
                break;
            }
        }

        // Ensure Lib\site-packages is on path
        if (!lines.Any(l => l.Trim().Equals("Lib\\site-packages", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add("Lib\\site-packages");
        }

        File.WriteAllLines(pthFile, lines);

        // Create Lib\site-packages directory so pip has somewhere to install
        Directory.CreateDirectory(Path.Combine(pythonDir, "Lib", "site-packages"));
    }

    private static async Task InstallPipAsync(string pythonDir, IProgress<SetupStep> progress, CancellationToken ct)
    {
        progress.Report(new SetupStep("Download get-pip.py…", 45));

        var getPipPath = Path.Combine(pythonDir, "get-pip.py");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(GetPipUrl, ct);
        await File.WriteAllBytesAsync(getPipPath, bytes, ct);

        progress.Report(new SetupStep("Installazione pip…", 50));

        var pythonExe = Path.Combine(pythonDir, "python.exe");
        await RunProcessAsync(pythonExe, ["-I", getPipPath, "--no-warn-script-location"], pythonDir,
            line => progress.Report(new SetupStep(line, 55)), ct);
        File.Delete(getPipPath);
    }

    private static async Task InstallBirdNetAsync(string pythonDir, IProgress<SetupStep> progress, CancellationToken ct)
    {
        progress.Report(new SetupStep("Installazione birdnet-analyzer (può richiedere qualche minuto)…", 60));

        var pipExe = Path.Combine(pythonDir, "Scripts", "pip.exe");
        var pythonExe = Path.Combine(pythonDir, "python.exe");

        void OnLine(string line)
        {
            // pip stampa "Downloading package-x.y.z.whl (X MB)" — lo usiamo come messaggio
            var msg = line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)
                      || line.StartsWith("Installing", StringComparison.OrdinalIgnoreCase)
                      || line.StartsWith("Collecting", StringComparison.OrdinalIgnoreCase)
                ? line : null;
            if (msg is not null)
                progress.Report(new SetupStep(msg, 65));
        }

        string[] pipArgs = [
            "install", BirdNetPackage,
            "--target", Path.Combine(pythonDir, "Lib", "site-packages"),
            "--no-warn-script-location",
            "--progress-bar", "off",
        ];

        if (File.Exists(pipExe))
            await RunProcessAsync(pipExe, pipArgs, pythonDir, OnLine, ct);
        else
            await RunProcessAsync(pythonExe, ["-m", "pip", .. pipArgs], pythonDir, OnLine, ct);

        progress.Report(new SetupStep("Configurazione completata.", 100));
    }

    private static async Task RunProcessAsync(
        string exe, string[] args, string workDir,
        Action<string>? onLine, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Impossibile avviare: {exe}");

        var stderr = new System.Text.StringBuilder();

        // Stream stdout in real time
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
                onLine?.Invoke(line);
        }, ct);

        // Collect stderr for error reporting
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync(ct)) is not null)
            {
                stderr.AppendLine(line);
                onLine?.Invoke(line);
            }
        }, ct);

        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Processo terminato con codice {proc.ExitCode}:\n{stderr}");
    }
}

public sealed record SetupStep(string Message, int ProgressPercent);
