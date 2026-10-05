<#
.SYNOPSIS
  Builds installer\out\Dispatch-Setup-<version>.exe.

.DESCRIPTION
  1. publishes Dispatch.exe (desktop app) and dispatch.exe (CLI) self-contained for 64-bit Windows, so the PC
     needs no .NET
  2. compiles Dispatch.iss with Inno Setup (installed with winget on first use if it's missing)

.EXAMPLE
  .\installer\build-installer.ps1 -Version 1.2.0
#>
param(
    [string]$Version = '0.1.0',
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must look like 1.2.0, got '$Version'." }

$here = $PSScriptRoot
$root = Split-Path $here -Parent
$publish = Join-Path $here 'out\publish'
$appOut = Join-Path $publish 'app'
$cliOut = Join-Path $publish 'cli'

function Invoke-Step([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE." }
}

function Find-Iscc {
    $dirs = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6", "$env:ProgramFiles\Inno Setup 6", "$env:ProgramFiles\Inno Setup 7",
        "${env:ProgramFiles(x86)}\Inno Setup 7", "$env:LOCALAPPDATA\Programs\Inno Setup 6", "$env:LOCALAPPDATA\Programs\Inno Setup 7")
    # Wherever it was installed: Inno Setup records its folder under Uninstall (machine-wide or just for this user).
    foreach ($hive in 'HKLM:\SOFTWARE\WOW6432Node', 'HKLM:\SOFTWARE', 'HKCU:\SOFTWARE') {
        foreach ($v in 6, 7) {
            $key = "$hive\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup $($v)_is1"
            $dirs += (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
        }
    }
    @((Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)) +
        ($dirs | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'ISCC.exe' }) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

# Find Inno Setup before the slow publish; install it once with winget if it isn't there.
if (-not $Iscc) { $Iscc = Find-Iscc }
if (-not $Iscc -and (Get-Command 'winget' -ErrorAction SilentlyContinue)) {
    Write-Host 'Inno Setup is not installed; installing it with winget (one time)...'
    winget install --id JRSoftware.InnoSetup --exact --silent --accept-package-agreements --accept-source-agreements
    $Iscc = Find-Iscc
}
if (-not $Iscc) {
    throw ('Inno Setup not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup), ' +
           'or pass its compiler: -Iscc "C:\path\to\ISCC.exe"')
}

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
foreach ($project in @(@{ Path = 'src\Dispatch.App'; Out = $appOut }, @{ Path = 'src\Dispatch.Cli'; Out = $cliOut })) {
    Invoke-Step 'dotnet' @(
        'publish', (Join-Path $root $project.Path),
        '-c', 'Release', '-r', 'win-x64', '--self-contained',
        "-p:Version=$Version", '-p:DebugType=none',
        '-o', $project.Out)
}

Invoke-Step $Iscc @("/DAppVersion=$Version", "/DPublishDir=$appOut", "/DCliPublishDir=$cliOut", (Join-Path $here 'Dispatch.iss'))
Write-Host "Done: $(Join-Path $here "out\Dispatch-Setup-$Version.exe")"
