<#
.SYNOPSIS
  Build, version and pack a Counterpick release, and optionally publish it to GitHub.

.DESCRIPTION
  One command from a clean checkout to an installer:

    .\tools\release.ps1              bump the patch version and pack it locally
    .\tools\release.ps1 -Publish     ...and publish it as a GitHub Release
    .\tools\release.ps1 -Version 1.2.0 -Publish
    .\tools\release.ps1 -NoBump      repack the version the csproj already has

  What it does, in order:
    1. Reads <Version> from src\Counterpick.App\Counterpick.App.csproj, bumps it (or
       takes yours), and writes it back so the version is tracked in git.
    2. dotnet publish, self-contained win-x64, with the update source baked into the
       assembly as the place the installed app checks for newer versions.
    3. vpk pack: writes Counterpick-win-Setup.exe, a portable zip, the full package and
       a delta from the previous release into the release folder.
    4. With -Publish: commits the version bump, pushes it, and uploads the packed
       release to GitHub as a published Release tagged v<version>.

  Without -Publish the build updates from the local release folder, which is how you try
  a release before anyone else sees it. With -Publish it updates from the GitHub
  Releases page, so whoever installed from a download gets the next version the same way.

  The first install is a double-click on Counterpick-win-Setup.exe. Every install after
  that is automatic: the app checks the source on startup, downloads a newer version in
  the background, and offers a restart. Nothing under %APPDATA%\Counterpick is touched by
  any of it.

.PARAMETER Version
  Exact version to release, x.y.z. Default: the csproj patch number plus one.

.PARAMETER Out
  Local release folder. Default: artifacts\releases in the repository, which git ignores.
  vpk needs the previous packages here to build a delta, so keep it between releases.

.PARAMETER Publish
  Commit and push the version bump, then upload the release to GitHub and publish it.
  Needs the GitHub CLI logged in (gh auth login) or GITHUB_TOKEN set.

.PARAMETER Repo
  The GitHub repository to publish to, and the update source baked into a published
  build. Default: https://github.com/markusweberg/counterpick

.PARAMETER NoBump
  Pack the csproj's current version without changing it.
#>
[CmdletBinding()]
param(
  [string]$Version,
  [string]$Out,
  [switch]$Publish,
  [string]$Repo = 'https://github.com/markusweberg/counterpick',
  [switch]$NoBump
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj = Join-Path $root 'src\Counterpick.App\Counterpick.App.csproj'
$icon = Join-Path $root 'src\Counterpick.App\Resources\counterpick.ico'
# Not $publish: PowerShell variable names are case-insensitive, so that name is the
# -Publish switch above and assigning a path to it fails before anything runs.
$publishDir = Join-Path $root 'artifacts\publish'
if (-not $Out) { $Out = Join-Path $root 'artifacts\releases' }
$Out = [IO.Path]::GetFullPath($Out)

# A published build must point at the Releases page, not at a folder on this machine -
# nobody else can read artifacts\releases. A local pack keeps pointing at the folder, so
# a release can be installed and updated here before it goes out.
$updateSource = if ($Publish) { $Repo } else { $Out }

# ── token, before anything is built ──────────────────────────────────────
# Fail here rather than after a five-minute publish with an installer nobody can upload.
$token = $null
if ($Publish) {
  $token = $env:GITHUB_TOKEN
  if (-not $token) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
      throw 'Publishing needs a GitHub token: install the GitHub CLI and run "gh auth login", or set GITHUB_TOKEN.'
    }
    $token = (gh auth token 2>$null | Out-String).Trim()
  }
  if (-not $token) { throw 'No GitHub token. Run "gh auth login", or set GITHUB_TOKEN.' }
}

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

# A tag that already exists means this version went out once already; packing over it
# would publish a second, different build under the same number. Ask the remote, not the
# local tag list: vpk creates the tag server-side when it publishes, so a machine that
# has not fetched since - including the one that cut the last release - looks innocent.
if ($Publish) {
  $existing = (git -C $root ls-remote --tags origin "refs/tags/v$Version" | Out-String).Trim()
  if ($existing) { throw "v$Version is already released. Pick a new version." }
}

if ($Version -ne $current) {
  ($xml -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>") | Set-Content $csproj -NoNewline
  Write-Host "Version $current -> $Version (written to the csproj)"
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
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Write-Host "Publishing to $publishDir ..."
dotnet publish $csproj -c Release -r win-x64 --self-contained -o $publishDir "-p:UpdateSource=$updateSource" --nologo -v quiet
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# ── pack ─────────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force $Out | Out-Null
Write-Host "Packing into $Out ..."
vpk pack -u Counterpick -v $Version -p $publishDir -e Counterpick.exe -o $Out `
  --packTitle Counterpick --packAuthors 'Markus Weberg' --icon $icon
if ($LASTEXITCODE) { throw 'vpk pack failed' }

# ── upload ───────────────────────────────────────────────────────────────
if ($Publish) {
  # The tag has to land on the commit that carries this version, so the bump goes in
  # first. Only the csproj is committed, by path: whatever else the author has staged or
  # in flight is none of this script's business and stays where it is.
  Write-Host ''
  Write-Host 'Committing the version bump and pushing ...'
  git -C $root diff --quiet HEAD -- $csproj
  if ($LASTEXITCODE -ne 0) {
    git -C $root commit -q -m "Release $Version" -- $csproj
    if ($LASTEXITCODE) { throw 'git commit failed' }
  } else {
    Write-Host "  nothing to commit, the csproj is already at $Version"
  }
  git -C $root push
  if ($LASTEXITCODE) { throw 'git push failed - the release was not uploaded' }

  Write-Host "Uploading to $Repo ..."
  vpk upload github -o $Out --repoUrl $Repo --token $token `
    --publish true --tag "v$Version" --releaseName "Counterpick $Version"
  if ($LASTEXITCODE) { throw 'vpk upload github failed' }

  Write-Host ''
  Write-Host "Counterpick $Version is published at $Repo/releases/latest"
  Write-Host '  Share:  the Counterpick-win-Setup.exe on that page.'
  Write-Host '  Already installed: the app picks it up on its next start, or Settings > Check for updates.'
} else {
  Write-Host ''
  Write-Host "Counterpick $Version is in $Out (not published)"
  Write-Host "  First install:  $(Join-Path $Out 'Counterpick-win-Setup.exe')"
  Write-Host '  This build updates from that folder. Re-run with -Publish to put it on GitHub.'
}
