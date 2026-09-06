using ImageGlass.Common;
using ImageGlass.Common.Photoing;
using ImageMagick;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private sealed record Result(string Name, bool ExpectedMatch, bool ActualMatch, bool Passed, string Detail);
    private static readonly List<Result> Results = [];
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static string Pixels(IMagickImage<float> image)
    {
        using var copy = image.Clone();
        copy.Depth = 8;
        return $"{copy.Width}x{copy.Height}:{Hash(copy.ToByteArray(MagickFormat.Rgba))}";
    }
    private static string Describe(IMagickImage<float> image) => $"{image.Format} {image.Width}x{image.Height} page={image.Page} disposal={image.GifDisposeMethod}";

    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        var report = Path.GetFullPath(args[2]);
        var icoPath = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(output);
        var mode = Environment.GetEnvironmentVariable("TEST_MODE")!;
        Check(mode is "before" or "fixed", "Unknown comparison mode");
        var libraryHash = Hash(File.ReadAllBytes(typeof(MagickCodec).Assembly.Location));
        Check(libraryHash.Equals(Environment.GetEnvironmentVariable("EXPECTED_LIB_SHA256"), StringComparison.OrdinalIgnoreCase), "Not testing the published library");
        Check(MagickNET.Version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1] == "14.17.1", "Wrong Magick package");
        var zipBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures.zip"));
        Check(Hash(zipBytes).Equals("ec733cb3a7ee517a7e1bad5af2fa7a153a38b2b976dfc3d3e6c1de2a2adfdda0", StringComparison.OrdinalIgnoreCase), "Fixture ZIP hash mismatch");
        using (var packed = new MemoryStream(zipBytes)) ZipFile.ExtractToDirectory(packed, root, true);
        var gif = Path.Combine(root, "reference", "07-original.gif");
        var disguised = Path.Combine(root, "07-optimized-gif-as-tif.tif");
        Check(File.ReadAllBytes(gif).SequenceEqual(File.ReadAllBytes(disguised)), "Input bytes differ");
        Core.Config.EnableAlwaysApplyColorProfile = false;
        Core.Config.ColorProfile = "None";
        using (var raw = new MagickImageCollection(gif))
        {
            Check(raw.Count == 5 && raw[0].Width == 64 && raw[0].Height == 48, "Wrong frame count or canvas");
            Check(raw[1].Width == 16 && raw[1].Height == 12 && raw[1].Page.X == 8 && raw[1].Page.Y == 8, "Fixture is not a delta-frame GIF");
            Check(raw[2].Width == 20 && raw[2].Height == 16 && raw[2].Page.X == 32 && raw[2].Page.Y == 20, "Wrong transparent frame");
            Console.WriteLine("INPUT: " + string.Join("; ", raw.Select(Describe)));
            raw.Coalesce();
            Check(MatchesExpected(raw, root), "Native coalesce disagrees with independent expected PNGs");
        }
        foreach (var source in new[] { gif, disguised })
        {
            var wrongExtension = source == disguised;
            foreach (var ext in new[] { ".tiff", ".gif" })
            {
                var name = (wrongExtension ? "renamed" : "canonical") + "-to" + ext;
                var expectedMatch = mode == "fixed" || !wrongExtension || ext == ".gif";
                try
                {
                    using var meta = await MagickCodec.LoadMetadataAsync(source);
                    Check(meta.FrameCount == 5, "Metadata lost frames");
                    var target = Path.Combine(output, name + ext);
                    await MagickCodec.SaveAsync(meta, target, new() { FrameIndex = -1, CorrectRotation = false });
                    using var actual = new MagickImageCollection(target);
                    Check(actual.Count == 5, "Save lost frames");
                    var stored = string.Join("; ", actual.Select(Describe));
                    // GIF stores deltas by design. TIFF must contain complete pages without repair on read.
                    if (ext == ".gif") actual.Coalesce();
                    var match = MatchesExpected(actual, root);
                    Results.Add(new(name, expectedMatch, match, expectedMatch == match, stored));
                    Console.WriteLine($"CASE: {mode}/{name}: complete_pixels={match}, expected={expectedMatch}; {stored}");
                }
                catch (Exception error)
                {
                    Results.Add(new(name, expectedMatch, false, false, error.ToString()));
                    Console.WriteLine($"FAIL: {mode}/{name}: {error}");
                }
            }
        }
        try
        {
            using var meta = await MagickCodec.LoadMetadataAsync(icoPath);
            var target = Path.Combine(output, "ico-to-tiff.tiff");
            await MagickCodec.SaveAsync(meta, target, new() { FrameIndex = -1, CorrectRotation = false });
            using var actual = new MagickImageCollection(target);
            using var expected = new MagickImageCollection(icoPath);
            var match = actual.Count == expected.Count && Enumerable.Range(0, expected.Count).All(i => Pixels(actual[i]) == Pixels(expected[i]));
            Results.Add(new("ico-to-tiff-preserve-sizes", true, match, match, string.Join("; ", actual.Select(Describe))));
        }
        catch (Exception error) { Results.Add(new("ico-to-tiff-preserve-sizes", true, false, false, error.ToString())); }
        var failures = Results.Count(r => !r.Passed);
        File.WriteAllText(report, JsonSerializer.Serialize(new { mode, source = Environment.GetEnvironmentVariable("SOURCE_SHA"), library_sha256 = libraryHash, magick = MagickNET.Version, results = Results, failures }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT: {Results.Count} GIF-save checks, {failures} unmet expectations");
        return failures == 0 ? 0 : 1;
    }

    private static bool MatchesExpected(MagickImageCollection actual, string root)
    {
        if (actual.Count != 5) return false;
        for (var i = 0; i < 5; i++)
        {
            using var expected = new MagickImage(Path.Combine(root, "reference", $"frame-{i + 1:00}.png"));
            if (Pixels(actual[i]) != Pixels(expected)) return false;
        }
        return true;
    }
}
