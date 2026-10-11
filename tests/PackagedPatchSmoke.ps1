param(
    [Parameter(Mandatory = $true)][string] $ProjectRoot,
    [Parameter(Mandatory = $true)][string] $Season1GameRoot,
    [Parameter(Mandatory = $true)][string] $Season2GameRoot,
    [Parameter(Mandatory = $true)][string] $PrivateModifiedBuildRoot
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectRoot).Path
$scratchParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = Join-Path $scratchParent ('CrowdPlay-packaged-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $tools = Join-Path $scratch 'tools'
    Expand-Archive -LiteralPath (Join-Path $project 'build\patch-tools.zip') -DestinationPath $tools
    foreach ($spec in @(
        @{ Season = 's1'; Game = $Season1GameRoot },
        @{ Season = 's2'; Game = $Season2GameRoot }
    )) {
        $season = $spec.Season
        $payload = Join-Path $scratch ($season + '-payload')
        $rebuilt = Join-Path $scratch ($season + '-rebuilt')
        Expand-Archive -LiteralPath (Join-Path $project ('build\' + $season + '-payload.zip')) -DestinationPath $payload
        & (Join-Path $tools 'python\python.exe') (Join-Path $tools 'apply_patches.py') `
            --game-root $spec.Game --season $season `
            --payload-dir $payload --output-dir $rebuilt `
            --xdelta (Join-Path $tools 'python\xdelta3.exe') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$season packaged patch helper failed." }
        $privateArchives = Join-Path $PrivateModifiedBuildRoot ($season + '\Archives')
        $count = 0
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $rebuilt 'Archives') -File) {
            $expected = Join-Path $privateArchives $file.Name
            if (-not (Test-Path -LiteralPath $expected)) { throw "Missing private comparison: $expected" }
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash) {
                throw "$season reconstructed file differs: $($file.Name)"
            }
            $count++
        }
        if ($count -ne 4) { throw "$season produced $count sidecar files; expected 4." }
        Write-Output "${season}: four packaged sidecars match the private last-working build."
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch).TrimEnd('\')
    if (-not $resolved.StartsWith($scratchParent + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('CrowdPlay-packaged-test-', [StringComparison]::Ordinal)) {
        throw 'The scratch path is outside the intended temporary directory.'
    }
    if (Test-Path -LiteralPath $resolved) {
        Get-ChildItem -LiteralPath $resolved -Recurse -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
        Get-ChildItem -LiteralPath $resolved -Recurse -Directory |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName }
        Remove-Item -LiteralPath $resolved
    }
}
