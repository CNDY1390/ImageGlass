using ImageGlass.Common.Photoing;
using ImageMagick;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

internal static class Program
{
    private const string FallbackErrorKey = "ImageGlass.ContentSniffFallbackException";
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Codec = typeof(MagickCodec);
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "imageglass-content-sniff-smoke"));
        Directory.CreateDirectory(root);
        var archive = Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.zip.b64")));
        Check(Convert.ToHexString(SHA256.HashData(archive)).Equals("680767d359b7d3be7f2c0f2526c5463dee2b53254636b784b19ab33128f02ec1", StringComparison.OrdinalIgnoreCase), "Fixture archive checksum mismatch");
        using (var zip = new MemoryStream(archive)) ZipFile.ExtractToDirectory(zip, root, true);
        var png = Path.Combine(root, "png-content.tif");
        var ico = Path.Combine(root, "png-first-frame.ico");
        var pngJpg = Path.Combine(root, "png-content.jpg");
        var normalPng = Path.Combine(root, "normal.png");
        var icoTif = Path.Combine(root, "ico-content.tif");
        File.Copy(png, pngJpg, true);
        File.Copy(png, normalPng, true);
        File.Copy(ico, icoTif, true);
        Console.WriteLine($"Magick.NET: {typeof(MagickImage).Assembly.FullName}");
        Console.WriteLine($"Native: {MagickNET.Version}");
        Console.WriteLine($"Fixtures: {root}");

        await Test("target-version ICO probe exposes embedded PNG frame", () =>
        {
            using var probe = new MagickImageCollection();
            probe.Ping(ico);
            Console.WriteLine("ICO probe: " + string.Join(", ", probe.Select(i => $"{i.Format} {i.Width}x{i.Height}")));
            Check(probe.Count == 4, "Expected four ICO frames");
            Check(probe[0].Format == MagickFormat.Png, "Expected the original ICO counterexample");
            return Task.CompletedTask;
        });

        foreach (var path in new[] { png, pngJpg })
        {
            await Test("baseline filename read rejects " + Path.GetFileName(path), async () =>
            {
                using var image = new MagickImage();
                Exception? failure = null;
                try { await image.ReadAsync(path, new MagickReadSettings(), CancellationToken.None); }
                catch (Exception error) { failure = error; }
                Check(failure is not null, "Baseline unexpectedly succeeded");
                PrintDiagnostics(failure!);
                Check(CanRetry(failure!), "Actual mismatch diagnostics do not match the retry rules");
            });
        }

        foreach (var path in new[] { png, pngJpg, normalPng, ico, icoTif })
        {
            await Test("single read " + Path.GetFileName(path), async () =>
            {
                using var expected = new MagickImage(path);
                var settings = new MagickReadSettings();
                using var actual = await ReadSingle(path, settings);
                Check(actual.Width == expected.Width && actual.Height == expected.Height, "Size changed");
                Check(actual.ToByteArray(MagickFormat.Rgba).SequenceEqual(expected.ToByteArray(MagickFormat.Rgba)), "Pixels changed");
                Check(settings.Format == MagickFormat.Unknown, "Caller settings were mutated");
            });
        }

        foreach (var path in new[] { ico, icoTif, png })
        {
            await Test("collection read " + Path.GetFileName(path), async () =>
            {
                using var expected = new MagickImageCollection(path);
                using var actual = await ReadCollection(path, new MagickReadSettings());
                Check(actual.Count == expected.Count, "Frame count changed");
                for (var i = 0; i < actual.Count; i++)
                {
                    Check(actual[i].Width == expected[i].Width && actual[i].Height == expected[i].Height, "Frame size changed");
                }
            });
        }

        foreach (var path in new[] { png, pngJpg, ico, icoTif })
        {
            await Test("real QuickDecode " + Path.GetFileName(path), async () =>
            {
                using var image = await MagickCodec.QuickDecodeAsync(path, 256, 256);
                Check(image is not null && image.Width > 0 && image.Height > 0, "QuickDecode returned no image");
            });
        }

        var gifTif = Path.Combine(root, "animated-gif-content.tif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 8, 8));
            frames.Add(new MagickImage(MagickColors.Blue, 8, 8));
            frames.Write(gifTif, MagickFormat.Gif);
        }
        await Test("multi-frame GIF with TIFF extension", async () =>
        {
            using var frames = await ReadCollection(gifTif, new MagickReadSettings());
            Check(frames.Count == 2, "Lost an animation frame");
        });

        await Test("associated warnings allowed but nested resource errors rejected", () =>
        {
            var error = new MagickCoderErrorException("primary");
            var warning = new MagickCoderWarningException("warning");
            error.SetRelatedException([warning]);
            Check(CanRetry(error), "Ordinary warning rejected");
            warning.SetRelatedException([new MagickResourceLimitErrorException("resource")]);
            Check(!CanRetry(error), "Nested resource error ignored");
            Check(!CanRetry(new IOException("io")), "IO error accepted");
            Check(!CanRetry(new MagickPolicyErrorException("policy")), "Policy error accepted");
            Check(!CanRetry(warning), "Top-level warning accepted");
            warning.SetRelatedException([error]);
            Check(CanRetry(error), "Cycle traversal failed");
            return Task.CompletedTask;
        });

        await Test("first success does not open fallback", async () =>
        {
            var instances = new List<FakeImage>();
            var fallbackCalls = 0;
            using var actual = await RunFake(png, new(), CancellationToken.None, instances,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _, _) => { fallbackCalls++; return Task.CompletedTask; });
            Check(instances.Count == 1 && fallbackCalls == 0 && !actual.Disposed, "Unnecessary retry or early disposal");
        });

        await Test("eligible failure uses a new instance and retains all settings", async () =>
        {
            var instances = new List<FakeImage>();
            var settings = new MagickReadSettings { Width = 27, Height = 31, FrameIndex = 2, FrameCount = 1 };
            var fallbackCalls = 0;
            using var actual = await RunFake(png, settings, CancellationToken.None, instances,
                (_, _, _, _) => Task.FromException(new MagickCoderErrorException("first")),
                (_, stream, passed, _) =>
                {
                    fallbackCalls++;
                    Check(ReferenceEquals(settings, passed), "Settings replaced or partially copied");
                    Check(passed.Format == MagickFormat.Unknown && passed.Width == 27 && passed.Height == 31 && passed.FrameIndex == 2 && passed.FrameCount == 1, "Options changed");
                    Check(stream.CanRead && stream.Position == 0, "Stream did not start at zero");
                    return Task.CompletedTask;
                });
            Check(instances.Count == 2 && instances[0].Disposed && !actual.Disposed && fallbackCalls == 1, "Incorrect ownership");
        });

        foreach (var scenario in new[] { "io", "policy", "associated-resource", "explicit-format" })
        {
            await Test("no retry: " + scenario, async () =>
            {
                var instances = new List<FakeImage>();
                var settings = new MagickReadSettings();
                Exception first = scenario switch
                {
                    "io" => new IOException("first"),
                    "policy" => new MagickPolicyErrorException("first"),
                    _ => new MagickCoderErrorException("first"),
                };
                if (scenario == "associated-resource") ((MagickException)first).SetRelatedException([new MagickResourceLimitErrorException("nested")]);
                if (scenario == "explicit-format") settings.Format = MagickFormat.Jpeg;
                var fallbackCalls = 0;
                Exception? caught = null;
                try
                {
                    using var result = await RunFake(png, settings, CancellationToken.None, instances,
                        (_, _, _, _) => Task.FromException(first),
                        (_, _, _, _) => { fallbackCalls++; return Task.CompletedTask; });
                }
                catch (Exception error) { caught = error; }
                Check(ReferenceEquals(caught, first), "Original exception lost");
                Check(instances.Count == 1 && instances[0].Disposed && fallbackCalls == 0, "Unexpected retry or leak");
            });
        }

        await Test("two failures retain the first error and attach the second", async () =>
        {
            var instances = new List<FakeImage>();
            var first = new MagickCoderErrorException("first");
            var second = new MagickCorruptImageErrorException("second");
            Exception? caught = null;
            try
            {
                using var result = await RunFake(png, new(), CancellationToken.None, instances,
                    (_, _, _, _) => Task.FromException(first),
                    (_, _, _, _) => Task.FromException(second));
            }
            catch (Exception error) { caught = error; }
            Check(ReferenceEquals(caught, first), "Primary error or identity lost");
            Check(ReferenceEquals(first.Data[FallbackErrorKey], second), "Fallback error lost");
            Check(instances.Count == 2 && instances.All(i => i.Disposed), "Failure instance leaked");
        });

        foreach (var stage in new[] { "before", "first-success", "first-failure", "fallback-success", "fallback-failure" })
        {
            await Test("cancellation wins: " + stage, async () =>
            {
                using var cts = new CancellationTokenSource();
                var instances = new List<FakeImage>();
                if (stage == "before") cts.Cancel();
                Exception? caught = null;
                try
                {
                    using var result = await RunFake(png, new(), cts.Token, instances,
                        (_, _, _, _) =>
                        {
                            if (stage.StartsWith("first-", StringComparison.Ordinal)) cts.Cancel();
                            return stage == "first-success" ? Task.CompletedTask : Task.FromException(new MagickCoderErrorException("first"));
                        },
                        (_, _, _, _) =>
                        {
                            cts.Cancel();
                            return stage == "fallback-success" ? Task.CompletedTask : Task.FromException(new MagickCoderErrorException("second"));
                        });
                }
                catch (Exception error) { caught = error; }
                Check(caught is OperationCanceledException, "Cancellation became a decode error");
                var expectedCount = stage == "before" ? 0 : stage.StartsWith("first-", StringComparison.Ordinal) ? 1 : 2;
                Check(instances.Count == expectedCount && instances.All(i => i.Disposed), "Cancelled read leaked or retried");
            });
        }

        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static Task<MagickImage> ReadSingle(string path, MagickReadSettings settings)
        => (Task<MagickImage>)Codec.GetMethod("ReadImageFileAsync__", PrivateStatic)!.Invoke(null, [path, settings, CancellationToken.None])!;

    private static Task<MagickImageCollection> ReadCollection(string path, MagickReadSettings settings)
        => (Task<MagickImageCollection>)Codec.GetMethod("ReadImageCollectionFileAsync__", PrivateStatic)!.Invoke(null, [path, settings, CancellationToken.None])!;

    private static bool CanRetry(Exception error)
        => (bool)Codec.GetMethod("CanRetryContentRead__", PrivateStatic)!.Invoke(null, [error])!;

    private static Task<FakeImage> RunFake(string path, MagickReadSettings settings, CancellationToken token,
        List<FakeImage> instances,
        Func<FakeImage, string, MagickReadSettings, CancellationToken, Task> readFile,
        Func<FakeImage, Stream, MagickReadSettings, CancellationToken, Task> readStream)
    {
        Func<FakeImage> create = () => { var item = new FakeImage(); instances.Add(item); return item; };
        return (Task<FakeImage>)Codec.GetMethod("ReadFileWithContentFallbackAsync__", PrivateStatic)!
            .MakeGenericMethod(typeof(FakeImage)).Invoke(null, [path, settings, token, create, readFile, readStream])!;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); _passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL: " + name + "\n" + error); }
    }

    private static void PrintDiagnostics(Exception error)
    {
        Console.WriteLine($"  {error.GetType().Name}: {error.Message}");
        if (error is MagickException magick)
        {
            foreach (var related in magick.RelatedExceptions) PrintDiagnostics(related);
        }
    }

    private sealed class FakeImage : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
