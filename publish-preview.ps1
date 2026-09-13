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
    [switch]$SkipZip,
    [switch]$StartupCheck
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDirectory = Join-Path $root 'publish/preview'
$zipPath = Join-Path $root 'publish/MultiBT-Preview-win-x64.zip'
$project = Join-Path $root 'src/MultiBT.App/MultiBT.App.csproj'

Write-Host '== MultiBT preview build ==' -ForegroundColor Cyan

# ---------------------------------------------------------------------------- localisation pre-flight
#
# Every localisation key the app asks for must exist in BOTH language tables.
#
# Localizer returns the key itself when a lookup misses, so a typo does not crash or log anything -- it
# ships a UI with the literal text "Status.SinkCleared" in it. That is exactly what happened: three keys
# were referenced that did not exist or were misspelled, and two more existed only in Chinese, so the
# English UI showed raw keys. Checking here makes it impossible to ship that again.
#
# EVERY App source file is scanned, not just the ViewModel. The recommended-device list names its keys from
# its own file, and a gate that only looked at one file would wave those straight through.
$localizerPath = Join-Path $root 'src/MultiBT.App/Localization/Localizer.cs'
$localizerText = Get-Content -LiteralPath $localizerPath -Raw

$appSources = Get-ChildItem -Path (Join-Path $root 'src/MultiBT.App') -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
    Where-Object { $_.Name -ne 'Localizer.cs' }

$usedKeys = @()
foreach ($source in $appSources) {
    $text = Get-Content -LiteralPath $source.FullName -Raw

    $usedKeys += [regex]::Matches(
        $text,
        'Instance\["([A-Za-z0-9_.]+)"\]|Instance\.Format\("([A-Za-z0-9_.]+)"|_localizer\["([A-Za-z0-9_.]+)"\]') |
        ForEach-Object {
            # No closing paren is required after Format("key": most calls pass arguments, so requiring one
            # silently skipped them and the check under-reported what it was guarding.
            @($_.Groups[1].Value, $_.Groups[2].Value, $_.Groups[3].Value) |
                Where-Object { $_ -ne '' } | Select-Object -First 1
        }

    # Key-shaped string literals, for tables that name their keys from plain data rather than a lookup --
    # the recommended-device list does exactly that, and a negative control proved the lookup patterns
    # above miss it entirely, so a typo there would have shipped.
    #
    # The pattern needs no exclusions: of the 76 dotted capitalised literals in the App layer, every one is
    # a localisation key. URLs and paths contain a separator and never match.
    $usedKeys += [regex]::Matches($text, '"([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9_]+)+)"') |
        ForEach-Object { $_.Groups[1].Value }
}

$usedKeys = $usedKeys | Sort-Object -Unique

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

# The XAML references keys too, and NOTHING here used to check them: the scan above only reads .cs files.
# That gap shipped a defect in this very version -- a step button's tooltip key was missing, so the UI
# displayed the literal text "Delay.Down.Tip". Keys in markup are just as capable of being wrong as keys in
# code, so both are now checked the same way.
$xamlPath = Join-Path $root 'src/MultiBT.App/MainWindow.xaml'
if (Test-Path $xamlPath) {
    $xamlText = Get-Content -LiteralPath $xamlPath -Raw
    $xamlKeys = [regex]::Matches($xamlText, 'Path=\[([A-Za-z0-9_.]+)\]') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

    $missingXaml = @()
    foreach ($key in $xamlKeys) {
        $occurrences = ([regex]::Matches($localizerText, [regex]::Escape('["' + $key + '"]'))).Count
        if ($occurrences -ne 2) { $missingXaml += "$key ($occurrences of 2)" }
    }

    if ($missingXaml.Count -gt 0) {
        Write-Host 'Keys referenced from MainWindow.xaml are not defined in both language tables:' -ForegroundColor Red
        $missingXaml | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host 'The UI would display the key itself rather than the text.' -ForegroundColor Red
        exit 1
    }

    Write-Host "Localisation: all $($xamlKeys.Count) XAML keys present in zh and en." -ForegroundColor Green
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
    '正前',            # position picker direction names (localiser table)
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

# ---------------------------------------------------------------------------- startup check
#
# A published build must actually RUN. Everything above can pass while the app throws on construction and
# shows an error dialog instead of a window -- which is exactly what happened: a null-forgiving dereference
# in a property that the UI reads during startup threw NullReferenceException, and the gate said nothing
# because it only ever inspected the binary as data.
#
# Deliberately run AFTER publishing, on the real artefact, and refusing to report success if it dies.
Write-Host ''
if ($StartupCheck)
{
    # A published build must actually RUN. Everything above can pass while the app throws on construction
    # and shows an error dialog instead of a window -- which is exactly what happened once: a null-forgiving
    # dereference in a property the UI reads during startup threw, and the gate said nothing because it only
    # ever inspected the binary as data.
    #
    # Opt-in, because it LAUNCHES the application. Whoever asked for a build wants to start it themselves,
    # and a build that opens the app fights with the session it was built for. Pass -StartupCheck when
    # "does it start" is the question being asked.
    Write-Host 'Starting the published build...' -ForegroundColor Cyan
    
    $probe = Start-Process -FilePath $executable -PassThru
    Start-Sleep -Seconds 14
    $probe.Refresh()
    
    if ($probe.HasExited) {
        Write-Host "FATAL: the published build exited on startup (code $($probe.ExitCode))." -ForegroundColor Red
        Write-Host 'It is not usable.' -ForegroundColor Red
        exit 1
    }
    
    # A crash during startup does NOT kill the process and does NOT set an informative window title: the
    # application shows a modal error dialog whose title is simply the application name, exactly as it appears
    # in a bug report. So check for a DIALOG by window class -- standard dialogs are class #32770, while the WPF
    # main window is not -- rather than trusting the title.
    Add-Type @'
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;
    public static class StartupProbe {
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        private delegate bool EnumProc(IntPtr h, IntPtr p);
        public static List<string> VisibleDialogClasses(uint targetPid) {
            var found = new List<string>();
            EnumWindows((h, _) => {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == targetPid && IsWindowVisible(h)) {
                    var sb = new StringBuilder(256);
                    GetClassName(h, sb, sb.Capacity);
                    if (sb.ToString() == "#32770") { found.Add(sb.ToString()); }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
'@
    
    $dialogs = [StartupProbe]::VisibleDialogClasses([uint32]$probe.Id)
    
    if ($dialogs.Count -gt 0) {
        Write-Host "FATAL: the published build is showing an error dialog on startup (title '$($probe.MainWindowTitle)')." -ForegroundColor Red
        Write-Host 'The build is not usable -- read the dialog text for the exception.' -ForegroundColor Red
        Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue
        exit 1
    }
    
    Write-Host "Startup OK: '$($probe.MainWindowTitle)'." -ForegroundColor Green
    Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
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
