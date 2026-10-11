param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}
$outputPath = (Resolve-Path -LiteralPath $OutputDirectory).Path
$hostExe = Join-Path $outputPath 'CrowdPlayHost.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ `
    '/reference:System.dll' `
    '/reference:System.Xml.dll' `
    '/reference:System.Web.Extensions.dll' `
    '/reference:System.Windows.Forms.dll' `
    "/resource:$(Join-Path $sourceDirectory 'assets\phone.html'),CrowdPlay.PhonePage" `
    "/resource:$(Join-Path $sourceDirectory 'assets\entry.html'),CrowdPlay.EntryPage" `
    "/resource:$(Join-Path $sourceDirectory 'assets\qr.html'),CrowdPlay.QrPage" `
    "/resource:$(Join-Path $sourceDirectory 'assets\qrcode-generator.js'),CrowdPlay.QrEncoder" `
    "/resource:$(Join-Path $sourceDirectory 'assets\qrcode-generator.LICENSE'),CrowdPlay.QrEncoderLicense" `
    "/out:$hostExe" `
    (Join-Path $sourceDirectory 'CrowdPlayHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'CrowdPlay host compilation failed.' }
Get-Item -LiteralPath $hostExe | Select-Object FullName, Length
