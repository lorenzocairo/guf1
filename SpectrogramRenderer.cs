using System.IO;
namespace Guf1;

public static class SpectrogramRenderer
{
    private const int WindowSize = 2048;
    private const int ZeroPaddingFactor = 2;
    private const int ImageHeight = 480;
    private const double MinFrequencyHertz = 1;
    private const double GainDecibels = 20;
    private const double RangeDecibels = 80;
    public const int DefaultMaxFrequencyHertz = 20000;

    public static SpectrogramData Render(string wavFilePath, double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
        {
            throw new ArgumentException("L'intervallo audio non e valido.");
        }

        var segment = WavSegmentReader.ReadMonoSegment(wavFilePath, startSeconds, endSeconds);
        if (segment.Samples.Length == 0)
        {
            throw new InvalidDataException("L'intervallo selezionato non contiene campioni audio.");
        }

        var pixels = CreateSpectrogramPixels(
            segment.Samples,
            segment.SampleRate,
            out var width,
            out var minFrequencyHertz,
            out var maxFrequencyHertz);
        return new SpectrogramData(width, ImageHeight, pixels, width * 4, minFrequencyHertz, maxFrequencyHertz);
    }

    private static byte[] CreateSpectrogramPixels(
        float[] samples,
        int sampleRate,
        out int width,
        out double minFrequencyHertz,
        out double maxFrequencyHertz)
    {
        var windowSize = Math.Min(WindowSize, NextPowerOfTwo(Math.Max(64, samples.Length)));
        var fftSize = NextPowerOfTwo(windowSize * ZeroPaddingFactor);
        var hopSize = Math.Max(1, windowSize / 16);
        width = Math.Max(1, 1 + Math.Max(0, samples.Length - 1) / hopSize);
        var decibelColumns = new double[width][];
        var window = CreateHannWindow(windowSize);
        var real = new double[fftSize];
        var imaginary = new double[fftSize];
        var nyquistFrequency = sampleRate / 2.0;
        maxFrequencyHertz = Math.Min(nyquistFrequency, DefaultMaxFrequencyHertz);
        minFrequencyHertz = Math.Min(MinFrequencyHertz, maxFrequencyHertz / 2);
        var minMel = HertzToMel(minFrequencyHertz);
        var maxMel = HertzToMel(maxFrequencyHertz);

        for (var x = 0; x < width; x++)
        {
            Array.Clear(real);
            Array.Clear(imaginary);

            var offset = x * hopSize;
            for (var i = 0; i < windowSize; i++)
            {
                var sampleIndex = offset + i;
                real[i] = sampleIndex < samples.Length ? samples[sampleIndex] * window[i] : 0;
            }

            FastFourierTransform(real, imaginary);

            var column = new double[ImageHeight];
            decibelColumns[x] = column;
            for (var y = 0; y < ImageHeight; y++)
            {
                var frequencyRatio = 1.0 - y / (double)(ImageHeight - 1);
                var frequency = MelToHertz(minMel + frequencyRatio * (maxMel - minMel));
                var exactBin = Math.Clamp(frequency / sampleRate * fftSize, 1, fftSize / 2 - 1);
                var lowerBin = (int)Math.Floor(exactBin);
                var upperBin = Math.Min(lowerBin + 1, fftSize / 2 - 1);
                var binFraction = exactBin - lowerBin;
                var lowerMagnitude = Math.Sqrt(real[lowerBin] * real[lowerBin] + imaginary[lowerBin] * imaginary[lowerBin]);
                var upperMagnitude = Math.Sqrt(real[upperBin] * real[upperBin] + imaginary[upperBin] * imaginary[upperBin]);
                var magnitude = (lowerMagnitude * (1 - binFraction) + upperMagnitude * binFraction) / Math.Max(1, windowSize / 2.0);
                var decibels = 20 * Math.Log10(magnitude + 1e-12);
                column[y] = decibels;
            }
        }

        var pixels = new byte[width * ImageHeight * 4];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < ImageHeight; y++)
            {
                var intensity = Math.Clamp((decibelColumns[x][y] + GainDecibels + RangeDecibels) / RangeDecibels, 0, 1);
                var gray = Colorize(intensity);
                var pixelIndex = (y * width + x) * 4;
                pixels[pixelIndex] = gray;
                pixels[pixelIndex + 1] = gray;
                pixels[pixelIndex + 2] = gray;
                pixels[pixelIndex + 3] = 255;
            }
        }

        return pixels;
    }

    private static double[] CreateHannWindow(int length)
    {
        var window = new double[length];
        if (length == 1)
        {
            window[0] = 1;
            return window;
        }

        for (var i = 0; i < length; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1));
        }

        return window;
    }

    private static byte Colorize(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        var contrasted = Math.Pow(clamped, 0.72);
        return (byte)Math.Round(218 - contrasted * 190);
    }

    private static double HertzToMel(double hertz) => 2595 * Math.Log10(1 + hertz / 700);

    private static double MelToHertz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static void FastFourierTransform(double[] real, double[] imaginary)
    {
        var n = real.Length;
        var bits = (int)Math.Log2(n);

        for (var i = 0; i < n; i++)
        {
            var j = ReverseBits(i, bits);
            if (j <= i)
            {
                continue;
            }

            (real[i], real[j]) = (real[j], real[i]);
            (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
        }

        for (var size = 2; size <= n; size <<= 1)
        {
            var halfSize = size / 2;
            var angle = -2 * Math.PI / size;
            var phaseStepReal = Math.Cos(angle);
            var phaseStepImaginary = Math.Sin(angle);

            for (var start = 0; start < n; start += size)
            {
                var phaseReal = 1.0;
                var phaseImaginary = 0.0;

                for (var i = 0; i < halfSize; i++)
                {
                    var evenIndex = start + i;
                    var oddIndex = evenIndex + halfSize;
                    var oddReal = real[oddIndex] * phaseReal - imaginary[oddIndex] * phaseImaginary;
                    var oddImaginary = real[oddIndex] * phaseImaginary + imaginary[oddIndex] * phaseReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextPhaseReal = phaseReal * phaseStepReal - phaseImaginary * phaseStepImaginary;
                    phaseImaginary = phaseReal * phaseStepImaginary + phaseImaginary * phaseStepReal;
                    phaseReal = nextPhaseReal;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        var reversed = 0;
        for (var i = 0; i < bits; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }
}
