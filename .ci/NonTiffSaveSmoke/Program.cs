using ImageGlass.Common;
using ImageGlass.Common.Photoing;
using ImageMagick;
using ImageMagick.Formats;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private sealed record Result(string Name, bool Passed, string Detail);
    private static readonly List<Result> Results = [];
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Pixels(IMagickImage<float> image)
    {
        using var copy = image.Clone();
        copy.Depth = 8;
        return $"{copy.Width}x{copy.Height}:{Hash(copy.ToByteArray(MagickFormat.Rgba))}";
    }
    private static MagickImageCollection StandardPages(string path)
    {
        var settings = new MagickReadSettings();
        settings.SetDefines(new TiffReadDefines { IgnoreLayers = true });
        return new MagickImageCollection(path, settings);
    }
    private static string Describe(string path)
    {
        using var standard = StandardPages(path);
        using var all = new MagickImageCollection(path);
        return $"standard_pages={standard.Count}; default_frames={all.Count}; standard={string.Join(";", standard.Select(Pixels))}; all={string.Join(";", all.Select(Pixels))}";
    }
    private static void ExclusiveOpen(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
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
    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        var report = Path.GetFullPath(args[2]);
        var existingFixtures = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(root, "reference"));
        var mode = Environment.GetEnvironmentVariable("LAYER_MODE")!;
        Check(mode is "before" or "fixed", "Unknown mode");
        var libHash = Hash(File.ReadAllBytes(typeof(MagickCodec).Assembly.Location));
        Check(libHash.Equals(Environment.GetEnvironmentVariable("EXPECTED_LIB_SHA256"), StringComparison.OrdinalIgnoreCase), "Wrong tested DLL");
        Check(MagickNET.Version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1] == "14.17.1", "Wrong package version");
        Core.Config.EnableAlwaysApplyColorProfile = false;
        Core.Config.ColorProfile = "None";

        var webp = Path.Combine(root, "reference", "08-original.webp");
        var renamed = Path.Combine(root, "08-two-full-frames-webp-as-tif.tif");
        using var expected = new MagickImageCollection();
        foreach (var color in new[] { MagickColors.Red, MagickColors.Blue })
        {
            var frame = new MagickImage(color, 32, 24) { Depth = 8, AnimationDelay = 100 };
            frame.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
            expected.Add(frame);
        }
        expected.Write(webp, MagickFormat.WebP);
        File.Copy(webp, renamed, true);
        for (var i = 0; i < expected.Count; i++) expected[i].Write(Path.Combine(root, "reference", $"webp-frame-{i+1:00}.png"), MagickFormat.Png);
        using (var input = new MagickImageCollection(webp))
        {
            Check(input.Count == 2 && Enumerable.Range(0, 2).All(i => input[i].Width == 32 && input[i].Height == 24 && Pixels(input[i]) == Pixels(expected[i])), "WebP fixture must contain two lossless full-canvas frames");
            Console.WriteLine("FIXTURE: " + string.Join(";", input.Select(i => $"{i.Format} {i.Width}x{i.Height} page={i.Page}")));
        }

        foreach (var source in new[] { webp, renamed })
        {
            var wrong = source == renamed;
            await Case((wrong ? "renamed" : "canonical") + "-webp-to-standard-tiff", async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(source);
                Check(meta.FrameCount == 2, "Metadata must contain both frames");
                var target = Path.Combine(output, (wrong ? "renamed" : "canonical") + ".tiff");
                await MagickCodec.SaveAsync(meta, target, new() { FrameIndex = -1, CorrectRotation = false });
                using var actual = StandardPages(target);
                var matches = actual.Count == 2 && Enumerable.Range(0, 2).All(i => Pixels(actual[i]) == Pixels(expected[i]));
                var expectedMatch = mode == "fixed" || !wrong;
                Check(matches == expectedMatch, "Unexpected standard-page/pixel result: " + Describe(target));
                if (!expectedMatch) Check(actual.Count == 1, "Expected a one-page TIFF regression, not an unrelated failure");
                ExclusiveOpen(source);
                ExclusiveOpen(target);
                return $"matches_expected_pixels={matches}; expected_match={expectedMatch}; {Describe(target)}";
            });
        }

        // Preserve the established true-TIFF save path, rather than changing its layer policy.
        foreach (var format in new[] { MagickFormat.Tiff, MagickFormat.Tiff64 })
        {
            await Case("true-" + format + "-save-unchanged", async () =>
            {
                var source = Path.Combine(root, "reference", "true-" + format + ".tiff");
                expected.Write(source, format);
                using var meta = await MagickCodec.LoadMetadataAsync(source);
                var options = new PhotoReadOptions { FrameIndex = -1, CorrectRotation = false };
                var settings = MagickCodec.ParseSettings(options, true, source);
                using var data = await MagickCodec.DecodeImageAsync(meta, options, settings, null, CancellationToken.None);
                Check(data.MultiFrames is not null && data.MultiFrames.Count > 0, "Missing TIFF collection");
                var decodedFormat = data.MultiFrames[0].Format;
                Check(MagickFormatInfo.Create(decodedFormat)?.ModuleFormat == MagickFormat.Tiff, "TIFF alias does not map to TIFF module");
                var control = Path.Combine(output, format + "-unchanged-control.tiff");
                await data.MultiFrames.WriteAsync(control);
                var target = Path.Combine(output, format + "-actual.tiff");
                await MagickCodec.SaveAsync(meta, target, options);
                var signature = Describe(target);
                Check(signature == Describe(control), "True TIFF layer or page behavior changed");
                ExclusiveOpen(target);
                return $"decoded={decodedFormat}; module=TIFF; {signature}";
            });
        }

        await Case("gif-and-ico-standard-page-controls", () =>
        {
            // Existing runners already performed writes; here independently ignore embedded layers on read.
            var gifOutput = Path.Combine(Path.GetFullPath(args[4]), "renamed-to.tiff.tiff");
            using var gifPages = StandardPages(gifOutput);
            Check(gifPages.Count == 5 && gifPages.All(i => i.Width == 64 && i.Height == 48), "GIF has hidden layers instead of five standard pages");
            var icoOutput = Path.Combine(Path.GetFullPath(args[4]), "ico-to-tiff.tiff");
            using var icoPages = StandardPages(icoOutput);
            Check(icoPages.Count == 4 && icoPages.Select(i => i.Width).SequenceEqual(new uint[] {256,128,64,32}), "ICO pages lost their independent sizes");
            return Task.FromResult("GIF: five standard pages; ICO: four independent standard pages");
        });

        var guard = typeof(MagickCodec).GetMethod("CanRetryContentRead__", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool CanRetry(Exception ex, MagickFormat format = MagickFormat.Unknown, string path = "sample.tif")
            => (bool)guard.Invoke(null, new object[] { ex, new MagickReadSettings { Format = format }, path })!;
        void Guard(string name, Exception error, bool want, MagickFormat format = MagickFormat.Unknown, string path = "sample.tif")
        {
            var actual = CanRetry(error, format, path);
            Results.Add(new(name, actual == want, $"actual={actual}; expected={want}; synthetic public exception API"));
            Console.WriteLine($"GUARD: {name}: actual={actual}, expected={want}");
        }
        Guard("guard/decoder", new MagickCoderErrorException("test"), true);
        Guard("guard/explicit-format", new MagickCoderErrorException("test"), false, MagickFormat.Png);
        Guard("guard/unknown-extension", new MagickCoderErrorException("test"), false, path: "sample.unrecognized_extension");
        Guard("guard/cancelled", new OperationCanceledException(), false);
        Guard("guard/io", new IOException("test"), false);
        foreach (var related in new MagickException[] { new MagickPolicyErrorException("test"), new MagickResourceLimitErrorException("test") })
        {
            var outer = new MagickCoderErrorException("test");
            outer.SetRelatedException([related]);
            Guard("guard/related-" + related.GetType().Name, outer, false);
        }
        var warning = new MagickCoderErrorException("test");
        warning.SetRelatedException([new MagickCoderWarningException("test")]);
        Guard("guard/related-warning", warning, true);
        var cycle = new MagickCoderErrorException("test");
        cycle.SetRelatedException([cycle]);
        Guard("guard/public-related-cycle", cycle, true);

        foreach (var all in new[] { false, true })
        {
            await Case("failure/" + (all ? "collection" : "single") + "-original-and-retry-errors", async () =>
            {
                var input = Path.Combine(existingFixtures, "corrupt.tif");
                Exception first;
                try
                {
                    if (all) { using var c = new MagickImageCollection(); await c.ReadAsync(input); }
                    else { using var i = new MagickImage(); await i.ReadAsync(input); }
                    throw new InvalidOperationException("Corrupt input unexpectedly decoded");
                }
                catch (MagickException error) { first = error; }
                using var meta = new PhotoMetadata(input);
                Exception caught;
                try
                {
                    using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = all ? -1 : 0 }, null, null, CancellationToken.None);
                    throw new InvalidOperationException("Corrupt input unexpectedly decoded");
                }
                catch (MagickException error) { caught = error; }
                Check(caught.GetType() == first.GetType() && caught.Message == first.Message, "Original diagnostic was replaced");
                Check(caught.Data["ImageGlass.ContentSniffFallbackException"] is Exception, "Retry error was lost");
                Check(caught.StackTrace?.Contains("ReadAsync") == true, "Original read stack missing");
                ExclusiveOpen(input);
                return $"original={caught.GetType().Name}; fallback={caught.Data["ImageGlass.ContentSniffFallbackException"]!.GetType().Name}; file released";
            });
        }
        var failures = Results.Count(r => !r.Passed);
        File.WriteAllText(report, JsonSerializer.Serialize(new { mode, source_sha = Environment.GetEnvironmentVariable("SOURCE_SHA"), source_tree = Environment.GetEnvironmentVariable("TESTED_TREE"), library_sha256 = libHash, magick = MagickNET.Version, fixtures = new { webp_sha256 = Hash(File.ReadAllBytes(webp)), renamed_sha256 = Hash(File.ReadAllBytes(renamed)) }, results = Results, failures, not_tested = new[] { "GUI", "in-flight cancellation", "hidden native IO faults", "real RAW", "PS/PDF", "Release/AOT" } }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT: {Results.Count} layer-save and diagnostic checks, {failures} unmet expectations");
        return failures == 0 ? 0 : 1;
    }
}
