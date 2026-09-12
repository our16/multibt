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
# string literals (button labels such as "自动对齐响度") are compiled into BAML as UTF-8 -- so a
# UTF-16-only check reports a false MISSING for anything that exists only in the UI markup.
$asUtf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
$asUtf8 = [System.Text.Encoding]::UTF8.GetString($bytes)

$markers = @(
    '端点音量',        # AutoMatchLevels / device-volume status messages (C# literal)
    '主设备',          # primary-device UI text (C# literal)
    '自动对齐响度',    # button label, XAML only -- exercises the BAML path
    '设备音量'         # slider label + ramp, added with the volume redesign (XAML + C#)
)

$missing = @()
foreach ($marker in $markers) {
    $inUtf16 = $asUtf16.Contains($marker)
    $inUtf8 = $asUtf8.Contains($marker)

    if ($inUtf16 -or $inUtf8) {
        $where = if ($inUtf16 -and $inUtf8) { 'metadata+BAML' } elseif ($inUtf16) { 'metadata' } else { 'BAML' }
        Write-Host "  found ($where): $marker" -ForegroundColor Green
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
