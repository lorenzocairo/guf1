using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Guf1;

/// <summary>
/// Carica on demand le miniature degli spettrogrammi per le card visibili.
/// Tutti i metodi pubblici vanno chiamati dal thread UI.
/// </summary>
public static class SpectrogramThumbnailLoader
{
    private const int ThumbnailWidth = 360;
    private const int ThumbnailHeight = 120;
    private const int MaxCachedThumbnails = 400;

    // Una card deve restare visibile per questo tempo prima che parta il calcolo: durante uno
    // scroll veloce le card che sfilano via non avviano nulla.
    private static readonly TimeSpan StartDelay = TimeSpan.FromMilliseconds(120);

    // Le richieste accodate entro questa finestra dalla piu' recente sono considerate "la schermata
    // attuale" e vengono servite dall'alto verso il basso.
    private static readonly long CurrentScreenWindowTicks = Stopwatch.Frequency / 5;

    // Limita i calcoli FFT concorrenti per non saturare la CPU durante lo scroll.
    private static readonly int MaxConcurrent = Math.Max(1, Environment.ProcessorCount / 2);
    private static readonly List<Request> Pending = [];
    private static int _running;

    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> CacheIndex = [];
    private static readonly LinkedList<CacheEntry> CacheOrder = new();

    /// <summary>La card e' diventata visibile: avvia il caricamento se e' la prima vista.</summary>
    public static void Attach(BirdNetResult result)
    {
        result.ViewCount++;
        if (result.ViewCount == 1)
        {
            _ = LoadAsync(result);
        }
    }

    /// <summary>La card non e' piu' visibile: annulla il lavoro in coda e libera l'immagine.</summary>
    public static void Detach(BirdNetResult result)
    {
        if (result.ViewCount > 0)
        {
            result.ViewCount--;
        }

        if (result.ViewCount > 0)
        {
            return;
        }

        result.LoadCts?.Cancel();
        result.LoadCts = null;
        result.ClearSpectrogram();
    }

    private static async Task LoadAsync(BirdNetResult result)
    {
        var cts = new CancellationTokenSource();
        result.LoadCts = cts;
        var token = cts.Token;

        var path = result.AudioPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            result.SetSpectrogramError("Percorso audio non disponibile");
            return;
        }

        var start = result.StartValue;
        var end = result.EndValue;
        var key = $"{path}|{start:R}|{end:R}";

        if (TryGetCached(key, out var cached))
        {
            result.SetSpectrogram(cached);
            return;
        }

        result.SetSpectrogramStatus("In coda...");

        try
        {
            await Task.Delay(StartDelay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Pending.Add(new Request(result, path, start, end, key, token, Stopwatch.GetTimestamp()));
        Pump();
    }

    /// <summary>Avvia richieste finche' ci sono slot liberi, scegliendo sempre le piu' attuali.</summary>
    private static void Pump()
    {
        while (_running < MaxConcurrent)
        {
            Pending.RemoveAll(request => request.Token.IsCancellationRequested);
            if (Pending.Count == 0)
            {
                return;
            }

            var next = PickNext();
            Pending.Remove(next);
            _running++;
            _ = RunAsync(next);
        }
    }

    private static Request PickNext()
    {
        // Schermata attuale = richieste arrivate a ridosso dell'ultima; tra queste la piu' in alto
        // (la prima arrivata). Le richieste vecchie, rimaste indietro, passano per ultime.
        var newest = Pending.Max(request => request.Stamp);
        return Pending
            .Where(request => newest - request.Stamp <= CurrentScreenWindowTicks)
            .MinBy(request => request.Stamp)!;
    }

    private static async Task RunAsync(Request request)
    {
        var result = request.Result;
        var token = request.Token;

        try
        {
            result.SetSpectrogramStatus("Calcolo spettrogramma...");
            var thumbnail = await Task.Run(
                () => RenderThumbnail(request.Path, request.Start, request.End, token),
                token);
            AddToCache(request.Key, thumbnail);
            if (!token.IsCancellationRequested)
            {
                result.SetSpectrogram(thumbnail);
            }
        }
        catch (OperationCanceledException)
        {
            // La card e' uscita dalla viewport: calcolo interrotto.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!token.IsCancellationRequested)
            {
                result.SetSpectrogramError(ex.Message);
            }
        }
        finally
        {
            _running--;
            Pump();
        }
    }

    private static BitmapSource RenderThumbnail(string path, double start, double end, CancellationToken token)
    {
        var data = SpectrogramRenderer.Render(path, start, end, token);
        var gray = new byte[ThumbnailWidth * ThumbnailHeight];

        // Riduzione a medie per blocchi: lo spettrogramma e' in scala di grigi (B = G = R).
        for (var ty = 0; ty < ThumbnailHeight; ty++)
        {
            var y0 = ty * data.Height / ThumbnailHeight;
            var y1 = Math.Min(data.Height, Math.Max(y0 + 1, (ty + 1) * data.Height / ThumbnailHeight));

            for (var tx = 0; tx < ThumbnailWidth; tx++)
            {
                var x0 = Math.Min(data.Width - 1, tx * data.Width / ThumbnailWidth);
                var x1 = Math.Min(data.Width, Math.Max(x0 + 1, (tx + 1) * data.Width / ThumbnailWidth));

                long sum = 0;
                for (var y = y0; y < y1; y++)
                {
                    var rowOffset = y * data.Stride;
                    for (var x = x0; x < x1; x++)
                    {
                        sum += data.Pixels[rowOffset + x * 4];
                    }
                }

                gray[ty * ThumbnailWidth + tx] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }

        var bitmap = BitmapSource.Create(
            ThumbnailWidth,
            ThumbnailHeight,
            96,
            96,
            PixelFormats.Gray8,
            null,
            gray,
            ThumbnailWidth);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool TryGetCached(string key, out ImageSource image)
    {
        if (CacheIndex.TryGetValue(key, out var node))
        {
            CacheOrder.Remove(node);
            CacheOrder.AddFirst(node);
            image = node.Value.Image;
            return true;
        }

        image = null!;
        return false;
    }

    private static void AddToCache(string key, ImageSource image)
    {
        if (CacheIndex.TryGetValue(key, out var existing))
        {
            CacheOrder.Remove(existing);
        }

        CacheIndex[key] = CacheOrder.AddFirst(new CacheEntry(key, image));

        while (CacheOrder.Count > MaxCachedThumbnails)
        {
            var last = CacheOrder.Last!;
            CacheOrder.RemoveLast();
            CacheIndex.Remove(last.Value.Key);
        }
    }

    private sealed record CacheEntry(string Key, ImageSource Image);

    private sealed record Request(
        BirdNetResult Result,
        string Path,
        double Start,
        double End,
        string Key,
        CancellationToken Token,
        long Stamp);
}
