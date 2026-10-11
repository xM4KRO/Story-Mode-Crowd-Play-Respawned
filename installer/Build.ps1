param(
    [Parameter(Mandatory = $true)]
    [string] $Season1PayloadZip,
    [Parameter(Mandatory = $true)]
    [string] $Season2PayloadZip,
    [Parameter(Mandatory = $true)]
    [string] $PatchToolsZip,
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,
    [switch] $TestBuild
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadPath1 = (Resolve-Path -LiteralPath $Season1PayloadZip).Path
$payloadPath2 = (Resolve-Path -LiteralPath $Season2PayloadZip).Path
$patchToolsPath = (Resolve-Path -LiteralPath $PatchToolsZip).Path
$compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework 4.8 C# compiler was not found.'
}
$iconPath = Join-Path $sourceDirectory 'assets\CrowdPlayInstaller.ico'
if (-not (Test-Path -LiteralPath $iconPath)) {
    throw 'The CrowdPlay installer icon was not found.'
}
if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}
$outputPath = (Resolve-Path -LiteralPath $OutputDirectory).Path
$uninstaller = Join-Path $outputPath 'UninstallCrowdPlay.exe'
$installer = Join-Path $outputPath 'InstallCrowdPlay.exe'
$allowedPaths = Join-Path $outputPath 'CrowdPlay.allowed.tmp'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = @()
foreach ($payloadPath in @($payloadPath1, $payloadPath2)) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($payloadPath)
    try {
        $payloadEntries = @($archive.Entries | Where-Object { $_.Name.Length -ne 0 } | ForEach-Object { $_.FullName })
        if (-not ($payloadEntries -contains 'install/cloudflared.exe') -or
            -not ($payloadEntries -contains 'install/CrowdPlayLAN/cloudflared.LICENSE') -or
            -not ($payloadEntries -contains 'patches/patch-manifest.json')) {
            throw "The payload is missing cloudflared, its license, or its patch manifest: $payloadPath"
        }
        foreach ($entry in $payloadEntries) {
            if ($entry.StartsWith('install/', [StringComparison]::Ordinal)) {
                $relative = $entry.Substring('install/'.Length)
                if ($relative.Length -eq 0) { throw "Empty install path in $payloadPath" }
                if ($relative.StartsWith('Archives/', [StringComparison]::Ordinal)) {
                    throw "Game archives must be reconstructed locally: $entry"
                }
                $entries += $relative
            }
            elseif (-not $entry.StartsWith('patches/', [StringComparison]::Ordinal)) {
                throw "Unexpected payload path: $entry"
            }
        }
    }
    finally { $archive.Dispose() }
}
$toolsArchive = [System.IO.Compression.ZipFile]::OpenRead($patchToolsPath)
try {
    $toolEntries = @($toolsArchive.Entries | Where-Object { $_.Name.Length -ne 0 } |
        ForEach-Object { $_.FullName })
    foreach ($required in @('python/python.exe', 'python/LICENSE.txt',
            'apply_patches.py', 'patch_pipeline.py', 'python/xdelta3.exe',
            'licenses/xdelta3.LICENSE', 'vendor/ttg-cli-inspector-LICENSE.txt')) {
        if ($toolEntries -notcontains $required) {
            throw "The patch tools are missing $required."
        }
    }
}
finally { $toolsArchive.Dispose() }
$entries += 'Archives/MCSM_pc_CrowdPlayLAN_Project.ttarch2'
$entries += 'Archives/MCSM_pc_CrowdPlayLAN_Menu.ttarch2'
$entries += 'Archives/MC2_pc_CrowdPlayLAN_Project.ttarch2'
$entries += 'Archives/MC2_pc_CrowdPlayLAN_Menu.ttarch2'
$entries += 'Archives/_resdesc_50_CrowdPlayLAN_Project.lua'
$entries += 'Archives/_resdesc_50_CrowdPlayLAN_Menu.lua'
$entries += 'CrowdPlayLAN/assets/StoryModeCursor.png'
$entries += 'CrowdPlayLAN/assets/VotingBanner.png'
$entries += 'CrowdPlayLAN/assets/StoryModeLogo.png'
$entries += 'UninstallCrowdPlay.exe'
$entries += 'CrowdPlayLAN/host-settings.xml'
$entries += 'CrowdPlayLAN/host-settings.next.xml'
$entries += 'CrowdPlayLAN/debug-report.txt'
[System.IO.File]::WriteAllLines($allowedPaths, [string[]]@($entries | Sort-Object -Unique),
    (New-Object System.Text.UTF8Encoding($false)))

$common = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+',
    "/win32icon:$iconPath",
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Xml.dll'
)
if ($TestBuild) { $common += '/define:TEST_BUILD' }

& $compiler $common "/resource:$allowedPaths,CrowdPlay.AllowedPaths" "/out:$uninstaller" `
    (Join-Path $sourceDirectory 'InstallCore.cs') `
    (Join-Path $sourceDirectory 'Uninstaller.cs')
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }

& $compiler $common `
    '/reference:System.IO.Compression.dll' `
    "/resource:$payloadPath1,CrowdPlay.Payload1" `
    "/resource:$payloadPath2,CrowdPlay.Payload2" `
    "/resource:$patchToolsPath,CrowdPlay.PatchTools" `
    "/resource:$(Join-Path $sourceDirectory 'assets\S1_Banner.png'),CrowdPlay.Banner1" `
    "/resource:$(Join-Path $sourceDirectory 'assets\S2_Banner.png'),CrowdPlay.Banner2" `
    "/resource:$uninstaller,CrowdPlay.Uninstaller" `
    "/out:$installer" `
    (Join-Path $sourceDirectory 'InstallCore.cs') `
    (Join-Path $sourceDirectory 'Installer.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Remove-Item -LiteralPath $allowedPaths

Get-Item -LiteralPath $installer, $uninstaller | Select-Object FullName, Length
