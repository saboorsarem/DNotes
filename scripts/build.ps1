<#
.SYNOPSIS
    Builds DNotes and runs its self-test.

.DESCRIPTION
    DNotes targets .NET Framework 4.8 and is built with the .NET SDK, but it is a
    WinForms app, not a .NET Core one. The SDK is only used as a compiler driver;
    the output is a single self-contained .exe that runs on any Windows machine
    with .NET Framework 4.8 or newer - which ships with Windows 10 and 11.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER SkipTest
    Build only. The self-test opens real windows and drives real mouse and
    keyboard input, so it needs an interactive desktop session.

.EXAMPLE
    .\scripts\build.ps1
    .\scripts\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root      = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $root 'src\DNotes.csproj'
$artifacts = Join-Path $root 'artifacts'

Write-Host ''
Write-Host '  DNotes build' -ForegroundColor Green
Write-Host "  configuration  $Configuration"
Write-Host "  project        $project"
Write-Host ''

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is required to build DNotes. Install it from https://dotnet.microsoft.com/download'
}

# The SDK refuses to build a .NET Framework project on a non-Windows host, and
# this project is WinForms throughout, so failing early with a clear message beats
# a wall of MSB errors.
if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
    throw 'DNotes is a Windows WinForms application and can only be built on Windows.'
}

& dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$exe = Join-Path $root "src\bin\$Configuration\net48\DNotes.exe"
if (-not (Test-Path $exe)) { throw "Expected the build to produce $exe but it is not there." }

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Copy-Item $exe       (Join-Path $artifacts 'DNotes.exe')       -Force
Copy-Item "$exe.config" (Join-Path $artifacts 'DNotes.exe.config') -Force -EA SilentlyContinue

$sizeKb = [math]::Round((Get-Item (Join-Path $artifacts 'DNotes.exe')).Length / 1KB)
Write-Host ''
Write-Host "  built  $artifacts\DNotes.exe  ($sizeKb KB)"

if ($SkipTest) {
    Write-Host '  self-test skipped' -ForegroundColor Yellow
    Write-Host ''
    exit 0
}

Write-Host ''
Write-Host '  running the self-test (this opens real windows)' -ForegroundColor Green
Write-Host ''

# The self-test drives real input through the Win32 queue, so it has to own the
# foreground. Anything else holding focus makes the keystroke checks flaky.
& (Join-Path $artifacts 'DNotes.exe') --selftest
$code = $LASTEXITCODE

Write-Host ''
if ($code -eq 0) {
    Write-Host '  SELF-TEST PASSED' -ForegroundColor Green
} else {
    Write-Host "  SELF-TEST FAILED (exit $code)" -ForegroundColor Red
}
Write-Host ''
exit $code
