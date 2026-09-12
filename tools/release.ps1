<#
.SYNOPSIS
  Build, version and pack a Counterpick release.

.DESCRIPTION
  One command from a clean checkout to an installer:

    .\tools\release.ps1              bump the patch version and pack it
    .\tools\release.ps1 -Version 1.2.0
    .\tools\release.ps1 -NoBump      repack the version the csproj already has

  What it does, in order:
    1. Reads <Version> from src\Counterpick.App\Counterpick.App.csproj, bumps it (or
       takes yours), and writes it back so the version is tracked in git.
    2. dotnet publish, self-contained win-x64, with the release folder baked into the
       assembly as the place the installed app checks for updates.
    3. vpk pack: writes Counterpick-win-Setup.exe, a portable zip, the full package and
       a delta from the previous release into the release folder.

  The first install is a double-click on Counterpick-win-Setup.exe. Every install after
  that is automatic: the app looks in the release folder on startup, downloads a newer
  version in the background, and offers a restart. Nothing under %APPDATA%\Counterpick
  is touched by any of it.

.PARAMETER Version
  Exact version to release, x.y.z. Default: the csproj patch number plus one.

.PARAMETER Out
  Release folder, and the one baked into the build as the update source. Default:
  artifacts\releases in the repository, which git ignores. Point it at a OneDrive folder
  to update a second PC from the same releases.

.PARAMETER NoBump
  Pack the csproj's current version without changing it.
#>
[CmdletBinding()]
param(
  [string]$Version,
  [string]$Out,
  [switch]$NoBump
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj = Join-Path $root 'src\Counterpick.App\Counterpick.App.csproj'
$icon = Join-Path $root 'src\Counterpick.App\Resources\counterpick.ico'
$publish = Join-Path $root 'artifacts\publish'
if (-not $Out) { $Out = Join-Path $root 'artifacts\releases' }
$Out = [IO.Path]::GetFullPath($Out)

# ── version ──────────────────────────────────────────────────────────────
$xml = Get-Content $csproj -Raw
if ($xml -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
  throw "No <Version>x.y.z</Version> in $csproj"
}
$current = "$($Matches[1]).$($Matches[2]).$($Matches[3])"

if ($Version) {
  if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z, got '$Version'" }
} elseif ($NoBump) {
  $Version = $current
} else {
  $Version = "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)"
}

if ($Version -ne $current) {
  ($xml -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>") | Set-Content $csproj -NoNewline
  Write-Host "Version $current -> $Version (written to the csproj; commit it with the release)"
} else {
  Write-Host "Version $Version"
}

# ── tooling ──────────────────────────────────────────────────────────────
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
  Write-Host 'Installing the Velopack CLI (dotnet tool install -g vpk)...'
  dotnet tool install -g vpk
  if ($LASTEXITCODE) { throw 'Could not install vpk' }
}

# ── publish ──────────────────────────────────────────────────────────────
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Write-Host "Publishing to $publish ..."
dotnet publish $csproj -c Release -r win-x64 --self-contained -o $publish "-p:UpdateSource=$Out" --nologo -v quiet
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# ── pack ─────────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force $Out | Out-Null
Write-Host "Packing into $Out ..."
vpk pack -u Counterpick -v $Version -p $publish -e Counterpick.exe -o $Out `
  --packTitle Counterpick --packAuthors 'Markus Weberg' --icon $icon
if ($LASTEXITCODE) { throw 'vpk pack failed' }

Write-Host ''
Write-Host "Counterpick $Version is in $Out"
Write-Host "  First install:  $(Join-Path $Out 'Counterpick-win-Setup.exe')"
Write-Host '  Already installed: the app picks it up on its next start, or Settings > Check for updates.'
