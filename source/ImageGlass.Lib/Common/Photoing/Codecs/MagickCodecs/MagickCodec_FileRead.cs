using ImageMagick;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ImageGlass.Common.Photoing;

public static partial class MagickCodec
{
    /// <summary>
    /// Reads a source image, retaining filename routing unless content sniffing is needed.
    /// </summary>
    private static Task<MagickImage> ReadImageFileAsync__(string filePath,
        MagickReadSettings settings, CancellationToken token)
        => ReadFileWithContentFallbackAsync__(filePath, settings, token,
            static () => new MagickImage(),
            static (image, path, options, ct) => image.ReadAsync(path, options, ct),
            static (image, stream, options, ct) => image.ReadAsync(stream, options, ct));


    /// <summary>
    /// Reads source frames with the same bounded fallback as the single-image path.
    /// </summary>
    private static Task<MagickImageCollection> ReadImageCollectionFileAsync__(string filePath,
        MagickReadSettings settings, CancellationToken token)
        => ReadFileWithContentFallbackAsync__(filePath, settings, token,
            static () => new MagickImageCollection(),
            static (images, path, options, ct) => images.ReadAsync(path, options, ct),
            static (images, stream, options, ct) => images.ReadAsync(stream, options, ct));


    /// <summary>
    /// Retries a failed filename read on a fresh instance without inventing a container format.
    /// </summary>
    private static async Task<T> ReadFileWithContentFallbackAsync__<T>(string filePath,
        MagickReadSettings settings, CancellationToken token, Func<T> create,
        Func<T, string, MagickReadSettings, CancellationToken, Task> readFile,
        Func<T, Stream, MagickReadSettings, CancellationToken, Task> readStream)
        where T : class, IDisposable
    {
        token.ThrowIfCancellationRequested();
        var image = create();
        try
        {
            await readFile(image, filePath, settings, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return image;
        }
        catch (Exception firstError)
        {
            image.Dispose();
            token.ThrowIfCancellationRequested();

            // Explicit overrides and errors unrelated to decoding must keep their original behavior.
            if (settings.Format != MagickFormat.Unknown
                || !CanRetryContentRead__(firstError)
                || MagickFormatInfo.Create(filePath) is null)
            {
                throw;
            }

            // A frame's Format can describe an embedded PNG rather than its surrounding ICO container.
            var fallback = create();
            try
            {
                using var stream = File.OpenRead(filePath);
                await readStream(fallback, stream, settings, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return fallback;
            }
            catch (Exception fallbackError)
            {
                fallback.Dispose();
                token.ThrowIfCancellationRequested();
                firstError.Data["ImageGlass.ContentSniffFallbackException"] = fallbackError;
            }

            // Preserve the original decoder error and stack, with the fallback error attached as data.
            throw;
        }
    }


    /// <summary>
    /// Allows only decode errors, including associated diagnostics, while tolerating ordinary warnings.
    /// </summary>
    private static bool CanRetryContentRead__(Exception error)
    {
        if (error is not (MagickCorruptImageErrorException or MagickCoderErrorException)) return false;

        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;

            if (current is not (MagickCorruptImageErrorException
                or MagickCoderErrorException or MagickWarningException)) return false;

            if (current is MagickException magickError)
            {
                foreach (var related in magickError.RelatedExceptions)
                {
                    pending.Push(related);
                }
            }

            if (current.InnerException is { } inner) pending.Push(inner);
        }

        return true;
    }
}
