$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_REPOSITORY -ne 'CNDY1390/ImageGlass' -or
    $env:GITHUB_REF -ne 'refs/heads/ci/content-sniff-fallback') {
    throw 'This preparation is restricted to the authorized personal-fork test branch.'
}

$path = 'source/ImageGlass.Lib/Common/Photoing/Codecs/MagickCodecs/MagickCodec.cs'
$text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$changes = @(
    @{
        Before = @'
            var imgColl = new MagickImageCollection();
            await imgColl.ReadAsync(meta.FilePath, settings, cancelToken);
'@
        After = @'
            var imgColl = await ReadImageCollectionFileAsync__(meta.FilePath, settings, cancelToken).ConfigureAwait(false);
'@
    },
    @{
        Before = @'
            await imgM.ReadAsync(meta.FilePath, settings, cancelToken);
'@
        After = @'
            imgM = await ReadImageFileAsync__(meta.FilePath, settings, cancelToken).ConfigureAwait(false);
'@
    },
    @{
        Before = @'
        var imgM = new MagickImage();
        try
        {
            await imgM.ReadAsync(filePath, settings, token);
            token.ThrowIfCancellationRequested();

            return imgM;
        }
        catch
        {
            imgM.Dispose();
            return null;
        }
'@
        After = @'
        try
        {
            return await ReadImageFileAsync__(filePath, settings, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
'@
    }
)

foreach ($change in $changes) {
    $before = $change.Before.Replace("`r`n", "`n")
    $after = $change.After.Replace("`r`n", "`n")
    $matches = [regex]::Matches($text, [regex]::Escape($before)).Count
    if ($matches -eq 1) {
        $text = $text.Replace($before, $after)
    }
    elseif ($matches -eq 0 -and [regex]::Matches($text, [regex]::Escape($after)).Count -eq 1) {
        Write-Host 'This source replacement is already applied.'
    }
    else {
        throw 'Unexpected source context; refusing an ambiguous replacement.'
    }
}

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($true))
& git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
& git diff --stat
& git add -- $path
& git diff --cached --quiet
if ($LASTEXITCODE -eq 1) {
    & git -c user.name='github-actions[bot]' -c user.email='41898282+github-actions[bot]@users.noreply.github.com' commit -m 'fix: use filename-first content fallback in single, collection and quick decode'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit source wiring.' }
    & git push origin HEAD:refs/heads/ci/content-sniff-fallback
    if ($LASTEXITCODE -ne 0) { throw 'Could not update the authorized test branch.' }
}
elseif ($LASTEXITCODE -ne 0) { throw 'Could not inspect staged changes.' }

$sha = & git rev-parse HEAD
Write-Host "Build source commit: $sha"
"BUILD_SOURCE_SHA=$sha" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
