param(
    [switch]$NoLaunch,
    [switch]$Verbose
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Get-Process BetterScene, BetterScene.Displays -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$verbosity = if ($Verbose) { 'normal' } else { 'quiet' }
dotnet build (Join-Path $root 'BetterScene.slnx') -p:Platform=x64 -v:$verbosity --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if ($NoLaunch) { Write-Host 'build ok'; return }

$exe = Join-Path $root 'src\BetterScene.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\BetterScene.exe'
if (-not (Test-Path $exe)) {
    $exe = Get-ChildItem -Path (Join-Path $root 'src\BetterScene.App\bin') -Recurse -Filter 'BetterScene.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $exe) { throw 'BetterScene.exe not found' }

Start-Process -FilePath $exe
Write-Host "build ok, started $exe"
