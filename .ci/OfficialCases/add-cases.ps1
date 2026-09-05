$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '../BaseMergeSmoke/Program.cs'
$text = [IO.File]::ReadAllText($path)
$versionAnchor = 'var magickVersion = magick.GetName().Version!.ToString(3);'
if ([regex]::Matches($text, [regex]::Escape($versionAnchor)).Count -ne 1) { throw 'Unexpected version check' }
# 14.17.1 reports AssemblyVersion 14.17.0.0; the library banner reports its package version.
$text = $text.Replace($versionAnchor, "var magickVersion = MagickNET.Version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];")
$prepareAnchor = '        var hashes = Directory.GetFiles(root)'
$prepare = @'
        using var officialCases = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("OFFICIAL_CASES")!));
        foreach (var sample in officialCases.RootElement.EnumerateArray())
        {
            var data = Convert.FromBase64String(sample.GetProperty("bytes").GetString()!);
            Check(Hash(data).Equals(sample.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Official sample checksum mismatch");
            File.WriteAllBytes(Path.Combine(root, sample.GetProperty("input").GetString()!), data);
            File.WriteAllBytes(Path.Combine(root, sample.GetProperty("canonical").GetString()!), data);
        }
'@
$testsAnchor = '        var unmet = Results.Count(r => !r.Met);'
$tests = @'
        foreach (var sample in officialCases.RootElement.EnumerateArray())
        {
            var input = PathFor(sample.GetProperty("input").GetString()!);
            var canonical = PathFor(sample.GetProperty("canonical").GetString()!);
            var count = sample.GetProperty("frames").GetInt32();
            var name = Path.GetFileName(input);
            await Observe("official/filename-api/" + name, "observe", async () =>
            {
                using var image = new MagickImage();
                await image.ReadAsync(input, new MagickReadSettings());
                using var expected = new MagickImage(canonical);
                Check(Pixels(image) == Pixels(expected), "API pixels differ");
                return Pixels(image);
            });
            for (var i = 0; i < count; i++)
            {
                var frame = i;
                await Observe("official/single/" + name + "/" + frame, Expected(true), async () =>
                {
                    using var meta = await MagickCodec.LoadMetadataAsync(input);
                    Check(meta.FrameCount == count, "Metadata frame count differs");
                    using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = frame, CorrectRotation = false }, null, null, CancellationToken.None);
                    using var expected = new MagickImageCollection(canonical);
                    Check(result.SingleFrame is not null && Pixels(result.SingleFrame) == Pixels(expected[frame]), "Frame pixels differ");
                    ExclusiveOpen(input);
                    return Pixels(result.SingleFrame!);
                });
            }
            await Observe("official/collection/" + name, Expected(true), async () =>
            {
                using var meta = await MagickCodec.LoadMetadataAsync(input);
                using var result = await MagickCodec.DecodeImageAsync(meta, new() { FrameIndex = -1, CorrectRotation = false }, null, null, CancellationToken.None);
                using var expected = new MagickImageCollection(canonical);
                Check(result.MultiFrames is not null && result.MultiFrames.Count == count, "Frame count differs");
                for (var i = 0; i < count; i++) Check(Pixels(result.MultiFrames![i]) == Pixels(expected[i]), "Collection pixels differ");
                ExclusiveOpen(input);
                return string.Join("; ", result.MultiFrames!.Select(Pixels));
            });
            await Observe("official/quick/" + name, Expected(true), async () =>
            {
                using var image = await MagickCodec.QuickDecodeAsync(input, 4096, 4096);
                using var expected = new MagickImage(canonical);
                Check(image is not null && Pixels(image) == Pixels(expected), "QuickDecode pixels differ or null");
                ExclusiveOpen(input);
                return Pixels(image!);
            });
            if (count == 1)
            {
                foreach (var ext in new[] { "png", "jpg" })
                {
                    await Observe("official/save/" + name + "/" + ext, Expected(true), async () =>
                    {
                        using var meta = await MagickCodec.LoadMetadataAsync(input);
                        var output = PathFor("official-saved." + ext);
                        await MagickCodec.SaveAsync(meta, output, new());
                        using var saved = new MagickImage(output);
                        using var expected = new MagickImage(canonical);
                        Check(saved.Width == expected.Width && saved.Height == expected.Height, "Saved dimensions differ");
                        Check(saved.Format == (ext == "png" ? MagickFormat.Png : MagickFormat.Jpeg), "Output format differs");
                        if (ext == "png") Check(Pixels(saved) == Pixels(expected), "Saved PNG pixels differ");
                        return Pixels(saved);
                    });
                }
            }
        }
'@
if ([regex]::Matches($text, [regex]::Escape($prepareAnchor)).Count -ne 1 -or [regex]::Matches($text, [regex]::Escape($testsAnchor)).Count -ne 1) { throw 'Unexpected harness anchors' }
$text = $text.Replace($prepareAnchor, $prepare + "`n" + $prepareAnchor).Replace($testsAnchor, $tests + "`n" + $testsAnchor)
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
