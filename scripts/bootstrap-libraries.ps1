#Requires -Version 7.0
<#
.SYNOPSIS
    Prepares the external LDraw and LDCad shadow data under Library/.

.DESCRIPTION
    The official parts library and the LDCad shadow library are independently maintained
    datasets and are deliberately not committed. This script makes acquiring them explicit
    and reproducible, and prints the exact revisions in use.

    Nothing is overwritten silently. Both the parts library and the shadow checkout are
    inputs to committed golden reports, so changing either can move a test baseline. Use
    -UpdateShadow or -Force to opt in to a change.

.PARAMETER Source
    Where to get the official library:
      Auto     Use whatever is already present, else fall back to LeoCAD. (default)
      Download Fetch a fresh complete.zip from library.ldraw.org.
      LeoCAD   Use the local LeoCAD library.bin.
      Path     Use -LibraryPath.

.PARAMETER LibraryPath
    An existing LDraw directory or ZIP to record as the library source. Used with -Source Path.

.PARAMETER UpdateShadow
    Fetch and fast-forward an existing shadow checkout. Without this, an existing checkout is
    left exactly as-is so baselines cannot move by accident.

.PARAMETER Force
    Replace an existing Library/complete.zip when downloading.

.EXAMPLE
    ./scripts/bootstrap-libraries.ps1
    Set up using whatever is already available locally.

.EXAMPLE
    ./scripts/bootstrap-libraries.ps1 -Source Download -Force
    Download a current complete.zip, replacing any existing one.
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Download', 'LeoCAD', 'Path')]
    [string]$Source = 'Auto',

    [string]$LibraryPath,

    [switch]$UpdateShadow,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$libraryDir = Join-Path $repoRoot 'Library'
$shadowDir = Join-Path $libraryDir 'LDCadShadowLibrary'
$completeZip = Join-Path $libraryDir 'complete.zip'
$extractedDir = Join-Path $libraryDir 'LDraw'
$leoCadLibrary = 'C:\Program Files\LeoCAD\library.bin'

$shadowRepo = 'https://github.com/RolandMelkert/LDCadShadowLibrary.git'
$completeUrl = 'https://library.ldraw.org/library/updates/complete.zip'

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Warn { param([string]$Message) Write-Host "    $Message" -ForegroundColor Yellow }

function Get-FileSha256 {
    param([string]$Path)
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Reads the library release stamp from LDConfig.ldr, whose !LDRAW_ORG header carries it,
# e.g. "Configuration UPDATE 2025-08-04".
function Get-LDrawUpdateTag {
    param([string]$Path)

    try {
        $lines = if (Test-Path -Path $Path -PathType Container) {
            $config = Join-Path $Path 'LDConfig.ldr'
            if (-not (Test-Path $config)) { $config = Join-Path $Path 'ldraw/LDConfig.ldr' }
            if (-not (Test-Path $config)) { return $null }
            Get-Content -Path $config -TotalCount 20
        }
        else {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
            try {
                $entry = $zip.Entries | Where-Object { $_.FullName -match '(^|/)LDConfig\.ldr$' } | Select-Object -First 1
                if (-not $entry) { return $null }
                $reader = New-Object System.IO.StreamReader($entry.Open())
                try { ($reader.ReadToEnd() -split "`n" | Select-Object -First 20) } finally { $reader.Dispose() }
            }
            finally { $zip.Dispose() }
        }

        foreach ($line in $lines) {
            if ($line -match '^\s*0\s+!LDRAW_ORG\s+(.+?)\s*$') { return $Matches[1] }
        }
    }
    catch {
        Write-Warn "Could not read the update tag from ${Path}: $($_.Exception.Message)"
    }

    return $null
}

New-Item -ItemType Directory -Path $libraryDir -Force | Out-Null

# ---------------------------------------------------------------------------
# LDCad shadow library
# ---------------------------------------------------------------------------
Write-Step 'LDCad shadow library'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Warn 'git is not on PATH; skipping the shadow checkout.'
}
elseif (Test-Path (Join-Path $shadowDir '.git')) {
    if ($UpdateShadow) {
        Write-Note 'Fetching and fast-forwarding the existing checkout.'
        git -C $shadowDir fetch --quiet origin
        git -C $shadowDir merge --ff-only --quiet FETCH_HEAD
    }
    else {
        Write-Note 'Already present. Pass -UpdateShadow to fetch; committed reports pin this revision.'
    }
}
else {
    Write-Note "Cloning $shadowRepo"
    git clone --quiet $shadowRepo $shadowDir
}

if (Test-Path (Join-Path $shadowDir '.git')) {
    $commit = (git -C $shadowDir rev-parse HEAD).Trim()
    $date = (git -C $shadowDir log -1 --format=%ad --date=short).Trim()
    $partCount = (Get-ChildItem -Path (Join-Path $shadowDir 'parts') -Filter *.dat -Recurse -ErrorAction SilentlyContinue).Count
    Write-Note "Commit : $commit ($date)"
    Write-Note "Parts  : $partCount shadow files"
    Write-Note 'License: CC BY-SA 4.0 - attribution required when redistributing this data.'
}

# ---------------------------------------------------------------------------
# Official LDraw parts library
# ---------------------------------------------------------------------------
Write-Step 'Official LDraw parts library'

$chosenPath = $null
$chosenVia = $null

switch ($Source) {
    'Path' {
        if (-not $LibraryPath) { throw '-Source Path requires -LibraryPath.' }
        if (-not (Test-Path $LibraryPath)) { throw "Not found: $LibraryPath" }
        $chosenPath = (Resolve-Path $LibraryPath).Path
        $chosenVia = '-LibraryPath'
    }

    'LeoCAD' {
        if (-not (Test-Path $leoCadLibrary)) { throw "Not found: $leoCadLibrary" }
        $chosenPath = $leoCadLibrary
        $chosenVia = 'LeoCAD library.bin'
    }

    'Download' {
        if ((Test-Path $completeZip) -and -not $Force) {
            throw "Library/complete.zip already exists. Pass -Force to replace it (this can move golden report baselines)."
        }

        Write-Note "Downloading $completeUrl"
        $temp = "$completeZip.download"
        try {
            Invoke-WebRequest -Uri $completeUrl -OutFile $temp -UseBasicParsing
            Move-Item -Path $temp -Destination $completeZip -Force
        }
        finally {
            if (Test-Path $temp) { Remove-Item $temp -Force }
        }

        $chosenPath = $completeZip
        $chosenVia = 'downloaded complete.zip'
    }

    'Auto' {
        # Mirrors the resolution order the CLI itself uses.
        if (Test-Path $completeZip) { $chosenPath = $completeZip; $chosenVia = 'Library/complete.zip' }
        elseif (Test-Path $extractedDir) { $chosenPath = $extractedDir; $chosenVia = 'Library/LDraw/' }
        elseif (Test-Path $leoCadLibrary) { $chosenPath = $leoCadLibrary; $chosenVia = 'LeoCAD library.bin' }
    }
}

if (-not $chosenPath) {
    Write-Warn 'No official library found.'
    Write-Warn 'Run with -Source Download, or place a complete.zip at Library/complete.zip.'
    exit 1
}

Write-Note "Source : $chosenVia"
Write-Note "Path   : $chosenPath"

$updateTag = Get-LDrawUpdateTag -Path $chosenPath
Write-Note "Update : $(if ($updateTag) { $updateTag } else { '(unknown)' })"

if (Test-Path -Path $chosenPath -PathType Leaf) {
    $size = (Get-Item $chosenPath).Length / 1MB
    Write-Note ("Size   : {0:N1} MiB" -f $size)
    Write-Note "SHA-256: $(Get-FileSha256 -Path $chosenPath)"
}

if ($chosenVia -eq 'LeoCAD library.bin') {
    Write-Warn 'LeoCAD ships a snapshot, not a tracked release. Prefer a current complete.zip'
    Write-Warn 'for results you intend to reproduce or publish.'
}

Write-Step 'Done'
Write-Note 'Verify with: dotnet run --project tools/TechnicsSim.Cli -- library-info'
