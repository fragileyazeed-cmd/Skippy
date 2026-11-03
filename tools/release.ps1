<#
.SYNOPSIS
  Builds Skippy, zips it and publishes it as a GitHub release, which the in-app "Check for updates" button picks up.
.DESCRIPTION
  1. Bump <Version> in src\Skippy\Skippy.csproj (e.g. 0.1.0 -> 0.2.0) and commit.
  2. Run:  powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Notes "What changed"
.NOTES
  Needs the GitHub CLI (gh) logged in. The zip goes to dist\ and is also what you send to friends.
#>
param([string]$Notes = "")

# Native tools (git, gh) print progress on stderr; Windows PowerShell 5.1 would treat that as fatal with
# 'Stop', so failures are detected through $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\Skippy\Skippy.csproj'
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) { $git = 'C:\Program Files\Git\cmd\git.exe' }

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
if (& $git -C $root status --porcelain) { throw "Commit your changes first (git status is not clean)." }
& $gh release view $tag *> $null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists. Bump <Version> in Skippy.csproj first." }

$out = Join-Path $root "dist\Skippy-$version"
$zip = Join-Path $root "dist\Skippy-$version-win-x64.zip"
$oldSetup = Join-Path $root "dist\SkippySetup-$version.exe"
if (Test-Path $oldSetup) { Remove-Item $oldSetup -Force }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Building $tag..."
dotnet publish $proj -c Release -o $out -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host ("Zipped {0:N1} MB -> {1}" -f ((Get-Item $zip).Length / 1MB), $zip)

# installer (what you send to friends); the zip is what the in-app updater downloads
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found (winget install JRSoftware.InnoSetup)." }
$setup = Join-Path $root "dist\SkippySetup-$version.exe"
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$out" (Join-Path $root 'installer\Skippy.iss')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setup)) { throw "Building the installer failed." }
Write-Host ("Installer {0:N1} MB -> {1}" -f ((Get-Item $setup).Length / 1MB), $setup)

& $git -C $root tag $tag
& $git -C $root push origin main $tag 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { throw "git push failed." }
if (-not $Notes) { $Notes = "Skippy $version" }
& $gh release create $tag $setup $zip --title "Skippy $version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
Write-Host "Released $tag. Everyone's 'Check for updates' button now offers it."
