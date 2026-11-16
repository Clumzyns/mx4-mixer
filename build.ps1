#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

$out = Join-Path $root 'dist'
if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

& dotnet publish (Join-Path $root 'src\Mixer\Mixer.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $out

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Join-Path $out 'AudioMixer.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Publish finished but $exe was not produced."
}

$bytes = (Get-Item $exe).Length
Write-Host ("Built {0} ({1:N1} MB)" -f $exe, ($bytes / 1MB))
