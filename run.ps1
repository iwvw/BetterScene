param(
    [switch]$Rebuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Get-Process BetterScene, BetterScene.Displays -ErrorAction SilentlyContinue | Stop-Process -Force

if ($Rebuild) {
    dotnet build (Join-Path $root 'BetterScene.slnx') -p:Platform=x64 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$exe = Get-ChildItem -Path (Join-Path $root 'src/BetterScene.App/bin') -Recurse -Filter 'BetterScene.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $exe) { throw 'BetterScene.exe not found, run with -Rebuild' }

Start-Process -FilePath $exe.FullName
Write-Host "started $($exe.FullName)"
