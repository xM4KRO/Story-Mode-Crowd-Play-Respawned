param(
    [Parameter(Mandatory = $true)][string] $TestRoot,
    [Parameter(Mandatory = $true)][string] $GameRoot,
    [Parameter(Mandatory = $true)][ValidateSet('Season1', 'Season2')][string] $Season,
    [Parameter(Mandatory = $true)][string] $Installer,
    [Parameter(Mandatory = $true)][string] $Uninstaller,
    [switch] $Reinstall
)

$ErrorActionPreference = 'Stop'
$safeParent = (Resolve-Path -LiteralPath $TestRoot).Path.TrimEnd('\')
$root = (Resolve-Path -LiteralPath $GameRoot).Path.TrimEnd('\')
if (-not $root.StartsWith($safeParent + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The smoke test game folder must be inside its isolated test root.'
}

$stockNames = if ($Season -eq 'Season1') {
    @('MCSM_pc_Project_data.ttarch2', 'MCSM_pc_Menu_data.ttarch2',
      'MCSM_pc_Project_txmesh.ttarch2')
} else {
    @('MC2_pc_Project_data.ttarch2', 'MC2_pc_Menu_data.ttarch2',
      'MC2_pc_Project_txmesh.ttarch2')
}
$originalHashes = @{}
foreach ($name in $stockNames) {
    $path = Join-Path $root (Join-Path 'Archives' $name)
    $originalHashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

$installerAssembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Installer).Path)
$program = $installerAssembly.GetType('CrowdPlayInstaller.InstallerProgram', $true)
$seasonType = $installerAssembly.GetType('CrowdPlayInstaller.GameSeason', $true)
$install = $program.GetMethod('Install', [Reflection.BindingFlags]'Static, NonPublic')
try {
    $install.Invoke($null, [object[]]@($root, [Enum]::Parse($seasonType, $Season))) | Out-Null
} catch [Reflection.TargetInvocationException] {
    throw $_.Exception.InnerException
}

$manifestPath = Join-Path $root 'CrowdPlay.install.xml'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Installer did not write a manifest.' }
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$installed = @($manifest.CrowdPlayInstall.File | ForEach-Object { $_.path })
foreach ($entry in $manifest.CrowdPlayInstall.File) {
    $path = Join-Path $root $entry.path
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing installed file: $($entry.path)" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($hash -ne $entry.sha256) { throw "Installed file hash differs: $($entry.path)" }
}
foreach ($required in @('CrowdPlayLAN\assets\VotingBanner.png',
        'CrowdPlayLAN\assets\StoryModeCursor.png', 'cloudflared.exe',
        'UninstallCrowdPlay.exe')) {
    if ($installed -notcontains $required) { throw "Missing manifest entry: $required" }
}
$bannerHash = if ($Season -eq 'Season1') {
    '3166B641257DAB0E2A42537E9956F1E8934EE736D6BCCFE47FDE80FEA60BB658'
} else {
    '56FE183A7E43FBBEA74D2F14BC9C2FFA8085D4AF14FF79BD7D5DA6DA8164103F'
}
if ((Get-FileHash -LiteralPath (Join-Path $root 'CrowdPlayLAN\assets\VotingBanner.png') -Algorithm SHA256).Hash -ne $bannerHash) {
    throw 'The installed voting banner differs from the matching season artwork.'
}
foreach ($group in @('Project', 'Menu')) {
    $prefix = if ($Season -eq 'Season1') { 'MCSM_pc_' } else { 'MC2_pc_' }
    $archive = "Archives\${prefix}CrowdPlayLAN_${group}.ttarch2"
    $descriptor = "Archives\_resdesc_50_CrowdPlayLAN_${group}.lua"
    if ($installed -notcontains $archive -or $installed -notcontains $descriptor) {
        throw "Missing reconstructed files for $group"
    }
}

if ($Reinstall) {
    try {
        $install.Invoke($null, [object[]]@($root, [Enum]::Parse($seasonType, $Season))) | Out-Null
    } catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
    [xml]$again = Get-Content -LiteralPath $manifestPath -Raw
    $againFiles = @($again.CrowdPlayInstall.File | ForEach-Object { $_.path })
    if ($againFiles.Count -ne $installed.Count) {
        throw 'Reinstallation changed the owned-file count.'
    }
}

$uninstallerAssembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Uninstaller).Path)
$uninstallerProgram = $uninstallerAssembly.GetType('CrowdPlayInstaller.UninstallerProgram', $true)
$perform = $uninstallerProgram.GetMethod('Perform', [Reflection.BindingFlags]'Static, NonPublic')
try {
    $perform.Invoke($null, [object[]]@($root)) | Out-Null
} catch [Reflection.TargetInvocationException] {
    throw $_.Exception.InnerException
}

foreach ($relative in $installed + @('CrowdPlay.install.xml')) {
    if (Test-Path -LiteralPath (Join-Path $root $relative)) {
        throw "Uninstaller left a mod file behind: $relative"
    }
}
foreach ($name in $stockNames) {
    $path = Join-Path $root (Join-Path 'Archives' $name)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $originalHashes[$name]) {
        throw "Uninstaller changed a stock archive: $name"
    }
}
Write-Output "$Season isolated install/uninstall passed; $($installed.Count) mod files removed, stock archives unchanged."
