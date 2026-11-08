namespace Guf1;

public sealed class BirdNetOptions
{
    // Paths
    public string InputFolder { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;

    // Location filter (set both to -1 to disable)
    public double Latitude { get; set; } = -1;
    public double Longitude { get; set; } = -1;

    // Week 1-48, -1 = auto from system date
    public int Week { get; set; } = -1;

    // Audio segmentation
    public double Overlap { get; set; } = 0.0;       // 0.0 – 2.9 s

    // Detection
    public double MinConfidence { get; set; } = 0.1;  // 0.01 – 0.99
    public double Sensitivity { get; set; } = 1.0;    // 0.5 – 1.5
    public double SfThreshold { get; set; } = 0.03;   // 0.05 – 0.99 (species freq filter)

    // Output
    public string ResultType { get; set; } = "csv";   // csv, table, audacity, r, kaleidoscope

    // Performance
    public int Threads { get; set; } = 4;
    public int BatchSize { get; set; } = 1;

    // Localisation
    public string Locale { get; set; } = "en";        // en, it, de, fr, …

    // Optional: path to custom species list (.txt, one species per line)
    public string CustomSpeciesList { get; set; } = string.Empty;

    // Optional: path to custom classifier (.tflite)
    public string CustomClassifier { get; set; } = string.Empty;

    public string[] ToArguments()
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "--output",      OutputFolder,
            "--lat",         Latitude.ToString("0.####", ic),
            "--lon",         Longitude.ToString("0.####", ic),
            "--week",        Week.ToString(),
            "--overlap",     Overlap.ToString("0.#", ic),
            "--min_conf",    MinConfidence.ToString("0.##", ic),
            "--sensitivity", Sensitivity.ToString("0.##", ic),
            "--sf_thresh",   SfThreshold.ToString("0.##", ic),
            "--rtype",       ResultType,
            "-t",            Threads.ToString(),
            "-b",            BatchSize.ToString(),
            "-l",            Locale,
        };

        if (!string.IsNullOrWhiteSpace(CustomSpeciesList))
        {
            args.Add("--slist");
            args.Add(CustomSpeciesList);
        }

        if (!string.IsNullOrWhiteSpace(CustomClassifier))
        {
            args.Add("-c");
            args.Add(CustomClassifier);
        }

        // INPUT è un argomento posizionale: deve essere l'ultimo
        args.Add(InputFolder);

        return [.. args];
    }
}
