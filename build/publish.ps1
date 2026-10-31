<#
.SYNOPSIS
    Builds Rondel as a single-file executable into .\dist\<runtime>\Rondel.exe.

.PARAMETER Runtime
    win-x64 (default), win-arm64 (Snapdragon, Surface Pro X) or all.

.PARAMETER Version
    Version stamped into the executable, e.g. 0.1.0. The release workflow passes the computed version.

.PARAMETER FrameworkDependent
    Much smaller exe that requires the .NET 10 Desktop Runtime on the target PC
    (Windows offers the download link on first start if it is missing).

.EXAMPLE
    .\build\publish.ps1
    .\build\publish.ps1 -Runtime all -Version 0.1.0
    .\build\publish.ps1 -FrameworkDependent
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'all')]
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.0.0',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Rondel.Windows\Rondel.Windows.csproj'
$runtimes = if ($Runtime -eq 'all') { @('win-x64', 'win-arm64') } else { @($Runtime) }

foreach ($rid in $runtimes) {
    $suffix = if ($FrameworkDependent) { '-framework-dependent' } else { '' }
    $output = Join-Path $root "dist\$rid$suffix"
    $publishArgs = @(
        'publish', $project,
        '-c', 'Release',
        '-r', $rid,
        '-o', $output,
        "-p:Version=$Version",
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=true',   # precompiled code: the menu is fast on first use
        '-p:DebugType=none'
    )
    if ($FrameworkDependent) {
        $publishArgs += @('--self-contained', 'false')
    }
    else {
        $publishArgs += @(
            '--self-contained', 'true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',   # WPF's native DLLs inside the one exe
            '-p:EnableCompressionInSingleFile=true'
        )
    }

    Write-Host "Publishing $rid$suffix $Version ..." -ForegroundColor Cyan
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    Get-ChildItem $output -Filter '*.exe' | ForEach-Object {
        Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green
    }
}
