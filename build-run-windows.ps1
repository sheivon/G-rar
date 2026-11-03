# Build and run G-RAR on Windows.
# Usage: .\build-run-windows.ps1 [-Release] [-Configuration Debug|Release] [-- app-args...]
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    [switch] $Release,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $AppArgs
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "src\G-RAR\G-RAR.csproj"

if ($Release) {
    $Configuration = "Release"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK not found. Install .NET 9 from https://dotnet.microsoft.com/download"
}

Write-Host "Building and running G-RAR ($Configuration) for Windows..."
$runArgs = @("run", "--project", $Project, "--configuration", $Configuration)
if ($AppArgs -and $AppArgs.Count -gt 0) {
    $runArgs += "--"
    $runArgs += $AppArgs
}

& dotnet @runArgs
exit $LASTEXITCODE
