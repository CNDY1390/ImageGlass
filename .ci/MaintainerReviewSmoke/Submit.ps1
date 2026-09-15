#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Repo = 'C:\Dev\ImageGlass-pr2446-local',
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$base = 'c5aad180e71eef4c7c6a0dea0b61646dc4eb95ed'
$expectedTree = 'a420927086ca997c03c6d67fe1cf333436f44ab3'
$oldBlob = 'd4e6368ebfb8bda7c45826b51bc5f536019f26c1'
$newBlob = '2289b3535f7625775eae67000955b13b625f8d6b'
$file = 'source/ImageGlass.Lib/Common/Photoing/Codecs/MagickCodecs/MagickCodec.cs'
$target = 'refs/heads/fix/reuse-detected-magick-format'
$patch = Join-Path $PSScriptRoot 'maintainer-review.patch'
$patchHash = '6b572d45853cc7e02d5c8410376ce13653af131db4a4c6a7c645fce252b1ff7d'

function Git {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $lines = @(& git -C $Repo @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed. Existing work was left in place." }
    return $lines
}
function GitText {
    param([Parameter(Mandatory)][string[]]$Arguments)
    return ((Git -Arguments $Arguments) -join "`n").Trim()
}

if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { throw "Repository not found: $Repo" }
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if (-not (Test-Path -LiteralPath $patch -PathType Leaf)) { throw 'maintainer-review.patch is missing. Extract the whole package first.' }
if ((Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash -ne $patchHash) { throw 'Patch checksum mismatch. Use the unmodified package.' }
$null = Get-Command git -ErrorAction Stop
$origin = GitText -Arguments @('remote', 'get-url', '--push', 'origin')
if ($origin -notmatch '^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)CNDY1390/ImageGlass(?:\.git)?$') {
    throw 'origin must point to the personal fork CNDY1390/ImageGlass. Nothing was changed.'
}
$branch = GitText -Arguments @('symbolic-ref', '--quiet', '--short', 'HEAD')
$head = GitText -Arguments @('rev-parse', 'HEAD')
$tree = GitText -Arguments @('rev-parse', 'HEAD^{tree}')
$dirty = GitText -Arguments @('status', '--porcelain=v1', '--untracked-files=all')
$needsApply = $false

if ($head -eq $base) {
    if ($dirty.Length -eq 0) {
        Git -Arguments @('apply', '--check', $patch) | Out-Host
        $needsApply = $true
    }
    else {
        # Resume a stopped signing attempt only when the only edit is the exact tested file.
        $extra = GitText -Arguments @('ls-files', '--others', '--exclude-standard')
        $changed = @(Git -Arguments @('diff', '--name-only', 'HEAD'))
        $workingBlob = GitText -Arguments @('hash-object', "--path=$file", '--', $file)
        $stagedBlob = GitText -Arguments @('rev-parse', ":$file")
        if ($extra.Length -gt 0 -or $changed.Count -ne 1 -or $changed[0] -ne $file -or
            $workingBlob -ne $newBlob -or $stagedBlob -notin @($oldBlob, $newBlob)) {
            throw 'Uncommitted changes differ from the tested patch. Preserve them separately; do not remove this check.'
        }
    }
}
else {
    $parent = GitText -Arguments @('rev-parse', 'HEAD^')
    if ($tree -ne $expectedTree -or $parent -ne $base -or $dirty.Length -gt 0) {
        throw "Unexpected local state: HEAD=$head, tree=$tree. Expected $base or its single tested follow-up commit. No reset was performed."
    }
}

$remoteLine = GitText -Arguments @('ls-remote', '--exit-code', '--refs', 'origin', $target)
$remoteHead = ($remoteLine -split '\s+')[0]
if ($remoteHead -ne $base -and $remoteHead -ne $head) {
    throw "Remote PR branch changed to $remoteHead. Stop here; do not force-push or delete this check."
}
if ($CheckOnly) {
    Write-Host "CHECK PASSED: $branch at $head. No local files, commits or remote refs were changed."
    return
}

if ($head -eq $base) {
    if ($needsApply) { Git -Arguments @('apply', $patch) | Out-Host }
    Git -Arguments @('add', '--', $file) | Out-Host
    Git -Arguments @('diff', '--cached', '--check') | Out-Host
    $stagedTree = GitText -Arguments @('write-tree')
    if ($stagedTree -ne $expectedTree) { throw "Staged tree $stagedTree differs from tested tree $expectedTree. Nothing was committed or pushed." }
    Git -Arguments @('diff', '--cached', '--stat') | Out-Host
    Git -Arguments @('commit', '-S', '-m', 'fix: exclude RAW decoders from content fallback', '-m', 'Remove unused fallback exception Data as requested in review 5199828960.') | Out-Host
    $head = GitText -Arguments @('rev-parse', 'HEAD')
}
Git -Arguments @('verify-commit', 'HEAD') | Out-Host
if ((GitText -Arguments @('rev-parse', 'HEAD^{tree}')) -ne $expectedTree -or
    (GitText -Arguments @('rev-parse', 'HEAD^')) -ne $base) { throw 'Unexpected signed commit content or parent. Not pushing.' }
Git -Arguments @('push', 'origin', "HEAD:$target") | Out-Host
Write-Host "Updated the existing PR with one signed follow-up: $head"
Write-Host "Commit: https://github.com/CNDY1390/ImageGlass/commit/$head"
Write-Host 'No force-push, new branch, PR edit, comment or merge was performed.'

try {
    $verified = $false
    for ($i = 0; $i -lt 5; $i++) {
        $info = Invoke-RestMethod -Uri "https://api.github.com/repos/CNDY1390/ImageGlass/commits/$head" -Headers @{ 'User-Agent' = 'ImageGlass-PR2446-check'; Accept = 'application/vnd.github+json' }
        if ($info.commit.verification.verified) { $verified = $true; break }
        Start-Sleep -Seconds 2
    }
    if ($verified) { Write-Host 'GitHub signature: Verified.' }
    else { Write-Warning "Push succeeded, but GitHub reports: $($info.commit.verification.reason). Check the commit page; do not create another commit merely to retry this check." }
}
catch { Write-Warning "Push succeeded; GitHub verification lookup failed. Check the commit page above. $($_.Exception.Message)" }
