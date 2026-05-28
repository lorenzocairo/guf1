using System.IO;
namespace Guf1;

public static class SpectrogramRenderer
{
    private const int FftSize = 2048;
    private const int ImageHeight = 360;
    private const double MinDecibels = -95;
    private const double MaxDecibels = -20;

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

        var pixels = CreateSpectrogramPixels(segment.Samples, segment.SampleRate, out var width);
        return new SpectrogramData(width, ImageHeight, pixels, width * 4);
    }

    private static byte[] CreateSpectrogramPixels(float[] samples, int sampleRate, out int width)
    {
        var fftSize = Math.Min(FftSize, NextPowerOfTwo(Math.Max(64, samples.Length)));
        var hopSize = Math.Max(1, fftSize / 4);
        width = Math.Max(1, 1 + Math.Max(0, samples.Length - 1) / hopSize);
        var pixels = new byte[width * ImageHeight * 4];
        var window = CreateHannWindow(fftSize);
        var real = new double[fftSize];
        var imaginary = new double[fftSize];
        var maxFrequency = Math.Min(sampleRate / 2.0, 12000);
        var maxBin = Math.Max(1, (int)Math.Floor(maxFrequency / sampleRate * fftSize));

        for (var x = 0; x < width; x++)
        {
            Array.Clear(real);
            Array.Clear(imaginary);

            var offset = x * hopSize;
            for (var i = 0; i < fftSize; i++)
            {
                var sampleIndex = offset + i;
                real[i] = sampleIndex < samples.Length ? samples[sampleIndex] * window[i] : 0;
            }

            FastFourierTransform(real, imaginary);

            for (var y = 0; y < ImageHeight; y++)
            {
                var frequencyRatio = 1.0 - y / (double)(ImageHeight - 1);
                var bin = Math.Clamp((int)Math.Round(frequencyRatio * maxBin), 0, fftSize / 2 - 1);
                var magnitude = Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]) / fftSize;
                var decibels = 20 * Math.Log10(magnitude + 1e-12);
                var intensity = Math.Clamp((decibels - MinDecibels) / (MaxDecibels - MinDecibels), 0, 1);
                var (r, g, b) = Colorize(intensity);
                var pixelIndex = (y * width + x) * 4;
                pixels[pixelIndex] = b;
                pixels[pixelIndex + 1] = g;
                pixels[pixelIndex + 2] = r;
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

    private static (byte R, byte G, byte B) Colorize(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        if (clamped < 0.25)
        {
            var t = clamped / 0.25;
            return (0, (byte)(32 * t), (byte)(48 + 96 * t));
        }

        if (clamped < 0.5)
        {
            var t = (clamped - 0.25) / 0.25;
            return (0, (byte)(32 + 128 * t), (byte)(144 - 80 * t));
        }

        if (clamped < 0.75)
        {
            var t = (clamped - 0.5) / 0.25;
            return ((byte)(210 * t), (byte)(160 + 70 * t), (byte)(64 - 48 * t));
        }

        var finalT = (clamped - 0.75) / 0.25;
        return ((byte)(210 + 45 * finalT), (byte)(230 + 25 * finalT), (byte)(16 + 220 * finalT));
    }

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
