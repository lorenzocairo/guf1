using System.Diagnostics;
using System.IO;

namespace Guf1;

public sealed class BirdNetRunner
{
    private readonly PythonEnvironment _env;

    public BirdNetRunner(PythonEnvironment env)
    {
        _env = env;
    }

    /// <summary>
    /// Runs BirdNET analyze.py with the given options.
    /// Reports each output line; throws on non-zero exit code.
    /// </summary>
    public async Task RunAsync(
        BirdNetOptions options,
        IProgress<string> output,
        CancellationToken ct = default)
    {
        if (!_env.IsReady)
            throw new InvalidOperationException("L'ambiente Python non è pronto. Esegui prima il setup.");

        if (!Directory.Exists(options.InputFolder))
            throw new ArgumentException($"Cartella input non trovata: {options.InputFolder}");

        Directory.CreateDirectory(options.OutputFolder);

        var psi = new ProcessStartInfo
        {
            FileName = _env.PythonExe,
            WorkingDirectory = Path.GetDirectoryName(_env.PythonExe),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Lancia come modulo: python -m birdnet_analyzer.analyze
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("birdnet_analyzer.analyze");

        // All options
        foreach (var arg in options.ToArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossibile avviare python.exe");

        // Stream stdout and stderr concurrently
        var stdoutTask = ReadStreamAsync(proc.StandardOutput, output, ct);
        var stderrTask = ReadStreamAsync(proc.StandardError, output, ct);

        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"BirdNET terminato con codice {proc.ExitCode}.");
    }

    private static async Task ReadStreamAsync(
        System.IO.TextReader reader,
        IProgress<string> output,
        CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                output.Report(line);
        }
    }
}
