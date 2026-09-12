#Requires -Version 7.0
<#
.SYNOPSIS
    Rebuilds the MultiBT preview that ships in publish/preview.

.DESCRIPTION
    There was no single reproducible command for refreshing the published preview, so the shipped
    executable silently went stale while the code moved on: a build in bin/Debug is NOT what the
    user double-clicks. This script exists to make that impossible to forget.

    It:
      1. stops any running MultiBT.App (otherwise the output file is locked),
      2. publishes a Release single-file win-x64 executable,
      3. re-creates the distributable zip,
      4. verifies the result actually contains this build's code.

    Framework-dependent by default is NOT what shipped before: the existing preview is ~191 MB, i.e.
    self-contained. Matching that is deliberate — a preview the user double-clicks should not depend
    on which runtime happens to be installed. Pass -FrameworkDependent for a ~2.5 MB build.

.PARAMETER FrameworkDependent
    Publish without bundling the runtime. Much smaller, but requires the .NET 10 Desktop Runtime.

.PARAMETER SkipZip
    Skip re-creating the distributable zip.

.EXAMPLE
    pwsh -File publish-preview.ps1
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDirectory = Join-Path $root 'publish/preview'
$zipPath = Join-Path $root 'publish/MultiBT-Preview-win-x64.zip'
$project = Join-Path $root 'src/MultiBT.App/MultiBT.App.csproj'

Write-Host '== MultiBT preview build ==' -ForegroundColor Cyan

# ---------------------------------------------------------------------------- localisation pre-flight
#
# Every localisation key the ViewModel asks for must exist in BOTH language tables.
#
# Localizer returns the key itself when a lookup misses, so a typo does not crash or log anything -- it
# ships a UI with the literal text "Status.SinkCleared" in it. That is exactly what happened: three keys
# were referenced that did not exist or were misspelled, and two more existed only in Chinese, so the
# English UI showed raw keys. Checking here makes it impossible to ship that again.
$localizerPath = Join-Path $root 'src/MultiBT.App/Localization/Localizer.cs'
$viewModelPath = Join-Path $root 'src/MultiBT.App/ViewModels/MainViewModel.cs'
$localizerText = Get-Content -LiteralPath $localizerPath -Raw
$viewModelText = Get-Content -LiteralPath $viewModelPath -Raw

$usedKeys = [regex]::Matches(
    $viewModelText,
    'Instance\["([A-Za-z0-9_.]+)"\]|Instance\.Format\("([A-Za-z0-9_.]+)"') |
    ForEach-Object { if ($_.Groups[1].Value) { $_.Groups[1].Value } else { $_.Groups[2].Value } } |
    Sort-Object -Unique

$missingKeys = @()
foreach ($key in $usedKeys) {
    $occurrences = ([regex]::Matches($localizerText, [regex]::Escape('["' + $key + '"]'))).Count
    if ($occurrences -ne 2) { $missingKeys += "$key ($occurrences of 2)" }
}

if ($missingKeys.Count -gt 0) {
    Write-Host 'Localisation keys are not defined in both language tables:' -ForegroundColor Red
    $missingKeys | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host 'A missing key is DISPLAYED as the key itself, so fix this before publishing.' -ForegroundColor Red
    exit 1
}

Write-Host "Localisation: all $($usedKeys.Count) ViewModel keys present in zh and en." -ForegroundColor Green

# A running instance holds a lock on the .exe and makes the publish fail with a confusing error.
$running = Get-Process -Name 'MultiBT.App' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping $($running.Count) running MultiBT.App process(es) so the output is not locked..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

Write-Host 'Publishing...' -ForegroundColor Cyan

$selfContained = -not $FrameworkDependent.IsPresent

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', $selfContained.ToString().ToLowerInvariant(),
    '-p:PublishSingleFile=true',
    '-o', $outputDirectory,
    '--nologo',
    '-v', 'quiet'
)

if ($selfContained) {
    # Keeps native libraries inside the single file, so the preview stays one executable.
    $arguments += '-p:IncludeNativeLibrariesForSelfExtract=true'
}

# Do NOT compress the bundled assemblies.
#
# The freshness check below works by finding this build's string literals in the produced binary.
# Compression makes that unreliable -- some literals survive a byte scan and others do not, so the
# check produced a false "stale" verdict. A slightly larger exe is a fair price for a check that can
# actually be trusted.
$arguments += '-p:EnableCompressionInSingleFile=false'

& dotnet @arguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $outputDirectory 'MultiBT.App.exe'
if (-not (Test-Path $executable)) {
    throw "Publish reported success but '$executable' is missing."
}

# ---------------------------------------------------------------------------- verify
# A stale preview is EXACTLY the failure this script exists to prevent, so check the produced
# binary really contains this build's strings rather than trusting the exit code.
Write-Host ''
Write-Host 'Verifying the published binary is current...' -ForegroundColor Cyan

$bytes = [System.IO.File]::ReadAllBytes($executable)

# Both encodings must be searched. C# string literals land in metadata as UTF-16, while XAML
# string literals are compiled into BAML as UTF-8 -- so a UTF-16-only check reports a false MISSING
# for anything that exists only in the UI markup.
#
# UTF-16 must ALSO be scanned at BOTH alignments. Decoding the whole file from offset 0 only finds
# literals that happen to start on an even byte; a literal at an odd offset is silently mis-decoded
# and reported as missing, which is a false "stale preview" verdict.
$asUtf16Even = [System.Text.Encoding]::Unicode.GetString($bytes)
$asUtf16Odd = [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
$asUtf8 = [System.Text.Encoding]::UTF8.GetString($bytes)

$markers = @(
    '端点音量',        # device-volume status messages (C# literal)
    '主设备',          # primary-device UI text (C# literal)
    '自动对齐响度',    # button label (XAML, via the localiser table)
    '设备音量'         # slider label, added with the volume redesign
)

$missing = @()
foreach ($marker in $markers) {
    $inEven = $asUtf16Even.Contains($marker)
    $inOdd = $asUtf16Odd.Contains($marker)
    $inUtf8 = $asUtf8.Contains($marker)

    if ($inEven -or $inOdd -or $inUtf8) {
        $where = @()
        if ($inEven -or $inOdd) { $where += 'metadata' }
        if ($inUtf8) { $where += 'BAML' }
        Write-Host "  found ($($where -join '+')): $marker" -ForegroundColor Green
    }
    else {
        Write-Host "  MISSING: $marker" -ForegroundColor Red
        $missing += $marker
    }
}

if ($missing.Count -gt 0) {
    throw "The published executable does not contain: $($missing -join ', '). The preview would be stale."
}

# ---------------------------------------------------------------------------- zip
if (-not $SkipZip) {
    Write-Host ''
    Write-Host 'Packaging zip...' -ForegroundColor Cyan

    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $zipPath
}

# ---------------------------------------------------------------------------- report
$info = Get-Item $executable

Write-Host ''
Write-Host '== Done ==' -ForegroundColor Cyan
Write-Host "  exe : $executable"
Write-Host "  size: $([math]::Round($info.Length / 1MB, 2)) MB"
Write-Host "  time: $($info.LastWriteTime)"

if (-not $SkipZip) {
    Write-Host "  zip : $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB)"
}

if ($FrameworkDependent) {
    Write-Host ''
    Write-Host '  NOTE: framework-dependent build - requires the .NET 10 Desktop Runtime.' -ForegroundColor Yellow
}
else {
    Write-Host ''
    Write-Host '  Self-contained: runs without .NET installed.' -ForegroundColor Green
}
