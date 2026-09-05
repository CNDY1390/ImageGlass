using ImageGlass.Common;
using ImageGlass.Common.Photoing;
using ImageMagick;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

internal static class Program
{
    private const string FallbackKey = "ImageGlass.ContentSniffFallbackException";
    private static int passed, failed;
    private static readonly MethodInfo RetryRule = typeof(MagickCodec).GetMethod("CanRetryContentRead__", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        var library = typeof(MagickCodec).Assembly.Location;
        var libraryHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library)));
        Console.WriteLine("Source commit: " + Environment.GetEnvironmentVariable("SOURCE_SHA"));
        Console.WriteLine("Library: " + library);
        Console.WriteLine("Library SHA256: " + libraryHash);
        Check(libraryHash.Equals(Environment.GetEnvironmentVariable("EXPECTED_LIB_SHA256"), StringComparison.OrdinalIgnoreCase), "Not testing the published library");
        Console.WriteLine("Magick.NET: " + typeof(MagickImage).Assembly.FullName);
        Console.WriteLine("Native: " + MagickNET.Version);
        Core.Config.EnableAlwaysApplyColorProfile = false;
        Core.Config.ColorProfile = "None";

        var archive = Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.zip.b64")));
        Check(Convert.ToHexString(SHA256.HashData(archive)).Equals("680767d359b7d3be7f2c0f2526c5463dee2b53254636b784b19ab33128f02ec1", StringComparison.OrdinalIgnoreCase), "Fixture checksum mismatch");
        using (var zip = new MemoryStream(archive)) ZipFile.ExtractToDirectory(zip, root, true);
        string PathFor(string name) => Path.Combine(root, name);
        var pngTif = PathFor("png-content.tif");
        var ico = PathFor("png-first-frame.ico");
        var normal = PathFor("normal.png");
        File.Copy(pngTif, normal, true);
        File.Copy(pngTif, PathFor("png-content.jpg"), true);
        File.Copy(ico, PathFor("ico-content.tif"), true);
        using (var source = new MagickImage(normal))
        {
            source.Write(PathFor("normal.jpg"), MagickFormat.Jpeg);
            source.Write(PathFor("normal.tif"), MagickFormat.Tiff);
            source.Write(PathFor("normal.webp"), MagickFormat.WebP);
        }
        File.Copy(PathFor("normal.webp"), PathFor("webp-content.jpg"), true);

        foreach (var name in new[] { "png-content.tif", "png-content.jpg", "webp-content.jpg" })
        {
            await Test("unpatched filename API rejects " + name, async () =>
            {
                using var image = new MagickImage();
                var error = await Failure(() => image.ReadAsync(PathFor(name), new MagickReadSettings()));
                Check(error is MagickException && CanRetry(error, new(), PathFor(name)), "Expected an eligible decoder error");
                Console.WriteLine("  " + error.GetType().Name + ": " + error.Message);
            });
        }

        foreach (var pair in new[] {
            ("png-content.tif", "normal.png"), ("png-content.jpg", "normal.png"),
            ("webp-content.jpg", "normal.webp"), ("normal.png", "normal.png"),
            ("normal.jpg", "normal.jpg"), ("normal.tif", "normal.tif"), ("normal.webp", "normal.webp") })
        {
            await Test("DecodeImageAsync single " + pair.Item1, async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(PathFor(pair.Item1));
                var settings = MagickCodec.ParseSettings(new(), false, meta.FilePath);
                using var result = await MagickCodec.DecodeImageAsync(meta, new() { CorrectRotation = false }, settings, null, CancellationToken.None);
                using var expected = new MagickImage(PathFor(pair.Item2));
                Check(result.SingleFrame is not null, "No single frame");
                EqualPixels(result.SingleFrame!, expected);
                Check(settings.Format == MagickFormat.Unknown, "Format modified");
                ExclusiveOpen(meta.FilePath);
            });
        }

        for (var i = 0; i < 4; i++)
        {
            var frame = i;
            await Test("DecodeImageAsync ICO frame " + (frame + 1) + "/4", async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(ico);
                Check(meta.FrameCount == 4, "ICO metadata lost frames");
                using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = frame, CorrectRotation = false }, null, null, CancellationToken.None);
                using var expected = new MagickImageCollection(ico);
                Check(result.SingleFrame is not null, "ICO frame missing");
                EqualPixels(result.SingleFrame!, expected[frame]);
                Console.WriteLine($"  frame {frame + 1}: {result.SingleFrame!.Width}x{result.SingleFrame.Height}");
            });
        }

        var gif = PathFor("normal.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 8, 8));
            frames.Add(new MagickImage(MagickColors.Blue, 8, 8));
            frames.Write(gif, MagickFormat.Gif);
        }
        File.Copy(gif, PathFor("gif-content.tif"), true);
        foreach (var pair in new[] { ("png-first-frame.ico", "png-first-frame.ico"), ("png-content.tif", "normal.png"), ("gif-content.tif", "normal.gif") })
        {
            await Test("DecodeImageAsync collection " + pair.Item1, async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(PathFor(pair.Item1));
                using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = -1, CorrectRotation = false }, null, null, CancellationToken.None);
                using var expected = new MagickImageCollection(PathFor(pair.Item2));
                Check(result.MultiFrames is not null && result.MultiFrames.Count == expected.Count, "Collection frame count changed");
                for (var i = 0; i < expected.Count; i++) EqualPixels(result.MultiFrames![i], expected[i]);
                ExclusiveOpen(meta.FilePath);
            });
        }

        foreach (var name in new[] { "png-content.tif", "png-content.jpg", "webp-content.jpg", "png-first-frame.ico" })
        {
            await Test("QuickDecodeAsync " + name, async () =>
            {
                using var image = await MagickCodec.QuickDecodeAsync(PathFor(name), 256, 256);
                Check(image is not null && image.Width > 0 && image.Height > 0, "QuickDecode returned null");
                ExclusiveOpen(PathFor(name));
            });
        }

        foreach (var ext in new[] { "png", "jpg" })
        {
            await Test("SaveAsync PNG-as-TIF to " + ext, async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(pngTif);
                var output = PathFor("saved-from-mismatch." + ext);
                await MagickCodec.SaveAsync(meta, output, new());
                using var saved = new MagickImage(output);
                using var expected = new MagickImage(normal);
                Check(saved.Width == expected.Width && saved.Height == expected.Height, "Saved dimensions changed");
                Check(saved.Format == (ext == "png" ? MagickFormat.Png : MagickFormat.Jpeg), "Wrong output format");
                if (ext == "png") EqualPixels(saved, expected);
            });
        }

        await Test("explicit TIFF override is not retried", async () =>
        {
            using var meta = await MagickCodec.LoadMetadataAsync(pngTif);
            var error = await Failure(async () => { using var result = await MagickCodec.DecodeImageAsync(meta, new(), new() { Format = MagickFormat.Tiff }, null, CancellationToken.None); });
            Check(error is MagickCoderErrorException && !error.Data.Contains(FallbackKey), "Explicit format bypassed");
        });

        foreach (var allFrames in new[] { false, true })
        {
            await Test("both decode failures preserved " + (allFrames ? "collection" : "single"), async () =>
            {
                var path = PathFor("corrupt.tif");
                File.WriteAllBytes(path, Enumerable.Repeat((byte)0x42, 64).ToArray());
                using var meta = new PhotoMetadata(path);
                var error = await Failure(async () => { using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = allFrames ? -1 : 0 }, null, null, CancellationToken.None); });
                Check(error is MagickCoderErrorException && error.Data[FallbackKey] is Exception, "Original or fallback diagnostic lost");
                ExclusiveOpen(path);
            });
            await Test("pre-cancelled decode " + (allFrames ? "collection" : "single"), async () =>
            {
                using var meta = new PhotoMetadata(pngTif);
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var error = await Failure(async () => { using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = allFrames ? -1 : 0 }, null, null, cts.Token); });
                Check(error is OperationCanceledException, "Cancellation became a decode error");
                ExclusiveOpen(pngTif);
            });
        }

        await Test("related-error retry filter", () =>
        {
            var error = new MagickCoderErrorException("primary");
            var warning = new MagickCoderWarningException("warning");
            error.SetRelatedException([warning]);
            Check(CanRetry(error, new(), pngTif), "Warning rejected");
            warning.SetRelatedException([new MagickResourceLimitErrorException("resource")]);
            Check(!CanRetry(error, new(), pngTif), "Nested resource error ignored");
            warning.SetRelatedException([error]);
            Check(CanRetry(error, new(), pngTif), "Cycle traversal failed");
            Check(!CanRetry(new IOException("io"), new(), pngTif), "IO accepted");
            Check(!CanRetry(new MagickPolicyErrorException("policy"), new(), pngTif), "Policy accepted");
            Check(!CanRetry(error, new() { Format = MagickFormat.Tiff }, pngTif), "Override ignored");
            Check(!CanRetry(error, new(), PathFor("no-extension")), "Unrecognized extension retried");
            return Task.CompletedTask;
        });

        await Test("existing limitation: renamed ICO fails before full decode", async () =>
        {
            using var probe = new MagickImage();
            var error = await Failure(() => { probe.Ping(PathFor("ico-content.tif")); return Task.CompletedTask; });
            Check(error is MagickException, "Re-evaluate the baseline limitation");
            using var quick = await MagickCodec.QuickDecodeAsync(PathFor("ico-content.tif"), 256, 256);
            Check(quick is null, "Unexpected probe behavior");
            Console.WriteLine("  LIMITATION: ICO renamed to .tif is still rejected by the unchanged metadata probe.");
        });
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
        Console.WriteLine("Not exercised: GUI interaction, real RAW, PS/PDF, concurrent cancellation races, hidden native stream-IO faults.");
        return failed == 0 ? 0 : 1;
    }

    private static bool CanRetry(Exception error, MagickReadSettings settings, string path)
        => (bool)RetryRule.Invoke(null, [error, settings, path])!;
    private static void EqualPixels(IMagickImage<float> actual, IMagickImage<float> expected)
    {
        Check(actual.Width == expected.Width && actual.Height == expected.Height, "Dimensions differ");
        Check(actual.ToByteArray(MagickFormat.Rgba).SequenceEqual(expected.ToByteArray(MagickFormat.Rgba)), "Pixels differ");
    }
    private static void ExclusiveOpen(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task<Exception> Failure(Func<Task> run)
    {
        try { await run(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure, but operation succeeded");
    }
    private static async Task Test(string name, Func<Task> run)
    {
        try { await run(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL: " + name + "\n" + error); }
    }
}
