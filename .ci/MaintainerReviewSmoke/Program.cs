using ImageGlass.Common;
using ImageGlass.Common.Photoing;
using ImageMagick;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private sealed record Result(string Name, bool Passed, string Detail);
    private static readonly List<Result> Results = [];
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    // Separate RAW reads use different native temporary filenames; compare all other diagnostic text.
    private static string StableRawMessage(Exception error) => Regex.Replace(error.Message, "`[^']*'", "`<native temporary path>'");
    private static string Pixels(IMagickImage<float> image)
    {
        using var copy = image.Clone();
        copy.Depth = 8;
        return $"{copy.Width}x{copy.Height}:{Hash(copy.ToByteArray(MagickFormat.Rgba))}";
    }
    private static async Task Case(string name, Func<Task<string>> test)
    {
        try
        {
            var detail = await test();
            Results.Add(new(name, true, detail));
            Console.WriteLine($"PASS: {name}: {detail}");
        }
        catch (Exception error)
        {
            Results.Add(new(name, false, error.ToString()));
            Console.WriteLine($"FAIL: {name}: {error}");
        }
    }
    private static void ExclusiveOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }
    private static async Task<MagickException> FilenameRejection(string path, bool collection)
    {
        try
        {
            if (collection)
            {
                using var image = new MagickImageCollection();
                await image.ReadAsync(path);
            }
            else
            {
                using var image = new MagickImage();
                await image.ReadAsync(path);
            }
        }
        catch (MagickException error)
        {
            Check(error is MagickCoderErrorException, "Fixture did not produce the review's libraw CoderError");
            return error;
        }
        throw new InvalidOperationException("Synthetic non-RAW TIFF was unexpectedly accepted as RAW");
    }
    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var report = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        var mode = Environment.GetEnvironmentVariable("REVIEW_MODE")!;
        Check(mode is "before" or "fixed", "Unknown review mode");
        var fixedMode = mode == "fixed";
        var libHash = Hash(File.ReadAllBytes(typeof(MagickCodec).Assembly.Location));
        Check(libHash.Equals(Environment.GetEnvironmentVariable("EXPECTED_LIB_SHA256"), StringComparison.OrdinalIgnoreCase), "Not testing published DLL");
        Check(MagickNET.Version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1] == "14.17.1", "Wrong Magick version");
        Core.Config.EnableAlwaysApplyColorProfile = false;
        Core.Config.ColorProfile = "None";

        var guard = typeof(MagickCodec).GetMethod("CanRetryContentRead__", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rawExtensions = Const.IMAGE_FORMATS.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(ext => MagickFormatInfo.Create("fixture" + ext)?.ModuleFormat == MagickFormat.Dng).ToArray();
        Check(rawExtensions.Length > 0, "No supported DNG-module extensions were found");
        foreach (var ext in rawExtensions)
        {
            await Case("module-guard/" + ext, () =>
            {
                var result = (bool)guard.Invoke(null, [new MagickCoderErrorException("synthetic libraw rejection"), new MagickReadSettings(), "fixture" + ext])!;
                Check(result == !fixedMode, "Wrong retry eligibility for DNG module " + ext);
                return Task.FromResult($"retry={result}; module=Dng; synthetic diagnostic");
            });
        }
        foreach (var ext in new[] { ".tif", ".jpg", ".png" })
        {
            await Case("non-raw-guard/" + ext, () =>
            {
                var result = (bool)guard.Invoke(null, [new MagickCoderErrorException("synthetic mismatch"), new MagickReadSettings(), "fixture" + ext])!;
                Check(result, "Non-RAW mismatch was unnecessarily blocked");
                return Task.FromResult("content retry remains eligible");
            });
        }

        // A deterministic rejected-RAW surrogate: valid RGB TIFF data, not a real camera RAW.
        var normal = Path.Combine(root, "09-control.tiff");
        var rejected = Path.Combine(root, "09-rejected-raw-container.dng");
        using (var image = new MagickImage(MagickColors.Red, 32, 24))
        {
            image.Depth = 8;
            image.Write(normal, MagickFormat.Tiff);
        }
        File.Copy(normal, rejected, true);
        using var expected = new MagickImage(normal);
        await Case("rejected-raw-surrogate/filename-versus-stream", async () =>
        {
            var first = await FilenameRejection(rejected, false);
            using var stream = File.OpenRead(rejected);
            using var detected = new MagickImage();
            await detected.ReadAsync(stream);
            Check(MagickFormatInfo.Create(detected.Format)?.ModuleFormat == MagickFormat.Tiff, "Stream did not choose TIFF");
            Check(Pixels(detected) == Pixels(expected), "Unexpected surrogate pixels");
            return $"filename={first.GetType().Name}: {first.Message}; stream={detected.Format}; ordinary TIFF bytes labelled .dng";
        });
        foreach (var all in new[] { false, true })
        {
            await Case("rejected-raw-surrogate/" + (all ? "collection" : "single"), async () =>
            {
                var first = await FilenameRejection(rejected, all);
                using var meta = new PhotoMetadata(rejected);
                MagickException? caught = null;
                try
                {
                    using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = all ? -1 : 0, CorrectRotation = false }, null, null, CancellationToken.None);
                    Check(!fixedMode, "Rejected RAW was silently accepted by content fallback");
                    var image = all ? result.MultiFrames![0] : result.SingleFrame!;
                    Check(Pixels(image) == Pixels(expected), "Unexpected old TIFF reinterpretation");
                }
                catch (MagickException error) { caught = error; }
                Check((caught is not null) == fixedMode, "Unexpected decode success/failure");
                if (caught is not null)
                {
                    Console.WriteLine($"RAW DIAGNOSTICS: filename={first.Message}; helper={caught.Message}");
                    Check(caught.GetType() == first.GetType() && StableRawMessage(caught) == StableRawMessage(first), "RAW diagnostic differs beyond its per-read temporary filename");
                    Check(!caught.Data.Contains("ImageGlass.ContentSniffFallbackException"), "Removed Data field reappeared");
                    Check(caught.StackTrace?.Contains("ReadAsync") == true, "Original read stack missing");
                }
                ExclusiveOpen(rejected);
                return fixedMode ? "original libraw rejection preserved (only random temporary path normalized); file released" : "old path silently accepts the rejected container as TIFF; file released";
            });
        }
        await Case("rejected-raw-surrogate/save-source-read", async () =>
        {
            using var meta = new PhotoMetadata(rejected);
            var target = Path.Combine(root, "surrogate-saved.tiff");
            MagickException? caught = null;
            try { await MagickCodec.SaveAsync(meta, target, new() { FrameIndex = -1, CorrectRotation = false }); }
            catch (MagickException error) { caught = error; }
            Check((caught is not null) == fixedMode, "Save source-read did not follow the RAW guard");
            Check(File.Exists(target) == !fixedMode, "Unexpected output creation");
            if (!fixedMode)
            {
                using var actual = new MagickImage(target);
                Check(Pixels(actual) == Pixels(expected), "Unexpected old saved pixels");
            }
            ExclusiveOpen(rejected);
            return fixedMode ? "save preserves RAW rejection and creates no output" : "old save accepts TIFF reinterpretation";
        });
        await Case("quick/rejected-raw-surrogate", async () =>
        {
            using var image = await MagickCodec.QuickDecodeAsync(rejected, 64, 64);
            Check(image is null, "Rejected RAW surrogate unexpectedly produced a thumbnail");
            return "null in both versions; this fixture is rejected by the earlier synchronous probe, not by the new guard";
        });
        var failures = Results.Count(r => !r.Passed);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            mode, source_sha = Environment.GetEnvironmentVariable("SOURCE_SHA"), source_tree = Environment.GetEnvironmentVariable("TESTED_TREE"),
            library_sha256 = libHash, magick = MagickNET.Version, raw_extensions = rawExtensions,
            fixture_sha256 = Hash(File.ReadAllBytes(rejected)), results = Results, failures,
            not_tested = new[] { "real camera RAW files", "GUI", "in-flight cancellation", "Release/AOT", "other platforms" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT: {Results.Count} maintainer-review checks, {failures} unmet expectations");
        return failures == 0 ? 0 : 1;
    }
}
