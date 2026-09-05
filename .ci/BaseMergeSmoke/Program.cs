using ImageGlass.Common;
using ImageGlass.Common.Photoing;
using ImageMagick;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private sealed record Observation(string Name, string Outcome, string Expectation, bool Met, string Detail);
    private static readonly List<Observation> Results = [];
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Pixels(IMagickImage<float> image) => $"{image.Width}x{image.Height} rgba:{Hash(image.ToByteArray(MagickFormat.Rgba))}";
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var reportPath = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        var kind = Environment.GetEnvironmentVariable("SOURCE_KIND")!;
        var sourceSha = Environment.GetEnvironmentVariable("SOURCE_SHA")!;
        var library = typeof(MagickCodec).Assembly.Location;
        var libraryHash = Hash(File.ReadAllBytes(library));
        var magick = typeof(MagickImage).Assembly;
        var magickVersion = magick.GetName().Version!.ToString(3);
        Console.WriteLine($"SOURCE: {kind} {sourceSha}");
        Console.WriteLine($"LIBRARY: {library} SHA256:{libraryHash}");
        Console.WriteLine($"MAGICK: {magick.FullName}");
        Console.WriteLine($"NATIVE: {MagickNET.Version}; {magick.GetCustomAttribute<AssemblyTrademarkAttribute>()?.Trademark}");
        Check(libraryHash.Equals(Environment.GetEnvironmentVariable("EXPECTED_LIB_SHA256"), StringComparison.OrdinalIgnoreCase), "Not testing the published library");
        Check(magickVersion == Environment.GetEnvironmentVariable("EXPECTED_MAGICK"), "Wrong loaded Magick.NET version");
        var zipBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures.zip"));
        Check(Hash(zipBytes).Equals("a7e23be15878158ec6895b8c74a85ca7825d4499014d7eed09cf0f826d83b96a", StringComparison.OrdinalIgnoreCase), "Fixture archive checksum mismatch");
        using (var zip = new MemoryStream(zipBytes)) ZipFile.ExtractToDirectory(zip, root, true);
        var hashes = Directory.GetFiles(root).OrderBy(p => p, StringComparer.Ordinal).ToDictionary(p => Path.GetFileName(p), p => Hash(File.ReadAllBytes(p)));
        foreach (var (name, hash) in hashes) Console.WriteLine($"FIXTURE: {name} {hash}");
        Core.Config.EnableAlwaysApplyColorProfile = false;
        Core.Config.ColorProfile = "None";
        string PathFor(string name) => Path.Combine(root, name);
        string Expected(bool mismatch) => mismatch && kind == "base" ? "observe" : "success";
        var pairs = new[] {
            (Input: "png-content.tif", Canonical: "normal.png"),
            (Input: "png-content.jpg", Canonical: "normal.png"),
            (Input: "webp-content.jpg", Canonical: "normal.webp"),
            (Input: "normal.png", Canonical: "normal.png"),
            (Input: "normal.jpg", Canonical: "normal.jpg"),
            (Input: "normal.tif", Canonical: "normal.tif"),
            (Input: "normal.webp", Canonical: "normal.webp") };

        foreach (var pair in pairs.Take(3))
        {
            await Observe("filename-api/" + pair.Input, "observe", async () =>
            {
                using var image = new MagickImage();
                await image.ReadAsync(PathFor(pair.Input), new MagickReadSettings());
                using var expected = new MagickImage(PathFor(pair.Canonical));
                Check(Pixels(image) == Pixels(expected), "Filename API returned different pixels");
                return Pixels(image);
            });
        }
        foreach (var pair in pairs)
        {
            await Observe("single/" + pair.Input, Expected(pair.Input != pair.Canonical), async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(PathFor(pair.Input));
                var settings = MagickCodec.ParseSettings(new(), false, meta.FilePath);
                using var actual = await MagickCodec.DecodeImageAsync(meta, new() { CorrectRotation = false }, settings, null, CancellationToken.None);
                using var expected = new MagickImage(PathFor(pair.Canonical));
                Check(actual.SingleFrame is not null, "No single frame");
                Check(Pixels(actual.SingleFrame!) == Pixels(expected), "Single decode returned different pixels");
                Check(settings.Format == MagickFormat.Unknown, "Format changed");
                ExclusiveOpen(meta.FilePath);
                return Pixels(actual.SingleFrame!);
            });
        }
        var ico = PathFor("png-first-frame.ico");
        for (var i = 0; i < 4; i++)
        {
            var frame = i;
            await Observe("ico/frame-" + (frame + 1), "success", async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(ico);
                Check(meta.FrameCount == 4, "ICO metadata lost frames");
                using var actual = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = frame, CorrectRotation = false }, null, null, CancellationToken.None);
                using var expected = new MagickImageCollection(ico);
                Check(actual.SingleFrame is not null && Pixels(actual.SingleFrame) == Pixels(expected[frame]), "ICO pixels or dimensions differ");
                return Pixels(actual.SingleFrame!);
            });
        }
        foreach (var pair in new[] { (Input: "png-first-frame.ico", Canonical: "png-first-frame.ico"), (Input: "png-content.tif", Canonical: "normal.png"), (Input: "gif-content.tif", Canonical: "normal.gif") })
        {
            await Observe("collection/" + pair.Input, Expected(pair.Input != pair.Canonical), async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(PathFor(pair.Input));
                using var actual = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = -1, CorrectRotation = false }, null, null, CancellationToken.None);
                using var expected = new MagickImageCollection(PathFor(pair.Canonical));
                Check(actual.MultiFrames is not null && actual.MultiFrames.Count == expected.Count, "Collection count differs");
                for (var i = 0; i < expected.Count; i++) Check(Pixels(actual.MultiFrames![i]) == Pixels(expected[i]), "Collection pixels differ");
                ExclusiveOpen(meta.FilePath);
                return string.Join("; ", actual.MultiFrames!.Select(Pixels));
            });
        }
        foreach (var pair in pairs.Take(3).Concat(new[] { (Input: "png-first-frame.ico", Canonical: "png-first-frame.ico") }))
        {
            await Observe("quick/" + pair.Input, Expected(pair.Input != pair.Canonical), async () =>
            {
                using var image = await MagickCodec.QuickDecodeAsync(PathFor(pair.Input), 256, 256);
                Check(image is not null && image.Width > 0 && image.Height > 0, "QuickDecode returned null");
                using var expected = new MagickImage(PathFor(pair.Canonical));
                Check(Pixels(image!) == Pixels(expected), "QuickDecode pixels differ");
                ExclusiveOpen(PathFor(pair.Input));
                return Pixels(image!);
            });
        }
        foreach (var ext in new[] { "png", "jpg" })
        {
            await Observe("save/png-content.tif-to-" + ext, Expected(true), async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(PathFor("png-content.tif"));
                var output = PathFor("saved." + ext);
                await MagickCodec.SaveAsync(meta, output, new());
                using var saved = new MagickImage(output);
                using var expected = new MagickImage(PathFor("normal.png"));
                Check(saved.Width == expected.Width && saved.Height == expected.Height, "Saved dimensions differ");
                Check(saved.Format == (ext == "png" ? MagickFormat.Png : MagickFormat.Jpeg), "Saved format differs");
                if (ext == "png") Check(Pixels(saved) == Pixels(expected), "Saved PNG pixels differ");
                return saved.Format + " " + Pixels(saved);
            });
        }
        foreach (var all in new[] { false, true })
        {
            var name = all ? "collection" : "single";
            await Observe("corrupt/" + name, "magick-error", async () =>
            {
                using var meta = new PhotoMetadata(PathFor("corrupt.tif"));
                using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = all ? -1 : 0 }, null, null, CancellationToken.None);
                return "Unexpected successful decode";
            });
            await Observe("pre-cancelled/" + name, "cancelled", async () =>
            {
                using var meta = new PhotoMetadata(PathFor("png-content.tif"));
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = all ? -1 : 0 }, null, null, cts.Token);
                return "Unexpected successful decode";
            });
        }
        await Observe("renamed-ico/probe", "observe", () =>
        {
            using var probe = new MagickImageCollection();
            probe.Ping(PathFor("ico-content.tif"));
            return Task.FromResult(string.Join("; ", probe.Select(i => $"{i.Format} {i.Width}x{i.Height}")));
        });
        var unmet = Results.Count(r => !r.Met);
        var report = new {
            source_kind = kind, source_sha = sourceSha, magick_version = magickVersion,
            library_sha256 = libraryHash, fixtures_sha256 = Hash(zipBytes), fixture_hashes = hashes,
            observations = Results, unmet_expectations = unmet,
            not_exercised = new[] { "GUI", "real RAW", "PS/PDF", "concurrent cancellation", "hidden native I/O faults" }
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT: {Results.Count} observations, {unmet} unmet expectations; baseline success was never required to fail.");
        return unmet == 0 ? 0 : 1;
    }

    private static async Task Observe(string name, string expectation, Func<Task<string>> run)
    {
        string outcome, detail;
        try { detail = await run(); outcome = "success"; }
        catch (Exception error) { outcome = error is OperationCanceledException ? "cancelled" : error is MagickException ? "magick-error" : "other-error"; detail = error.ToString(); }
        var met = expectation == "observe" || expectation == outcome;
        Results.Add(new(name, outcome, expectation, met, detail));
        Console.WriteLine($"OBS: {name} = {outcome} (expected {expectation}, met {met})");
        Console.WriteLine(detail);
    }
    private static void ExclusiveOpen(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
}
