<#
.SYNOPSIS
  构建 BetterScene 发布产物：便携版 zip 与 Inno Setup 安装版 exe。

.DESCRIPTION
  两种发行变体：
    - merged（合并版）：.NET 与 Windows App SDK 运行时随包携带，开箱即用，体积大。
    - split（分离版）：框架依赖，体积小，但要求机器已装 .NET 10 Desktop Runtime 与
      Windows App SDK Runtime；安装包会自动引导安装这两个运行时。
  每个变体产出便携 zip 与安装 exe（arm64 只出便携版）。
  版本号默认取 Directory.Build.props 的 VersionPrefix，可用 -Version 覆盖（CI 传 tag）。

.PARAMETER Variant
  merged / split / all，默认 all。

.PARAMETER Arch
  x64 或 arm64，默认 x64。

.PARAMETER Version
  覆盖版本号，如 0.1.0。

.PARAMETER Configuration
  默认 Release。

.PARAMETER SkipInstaller
  只产出便携 zip，不编译安装包。

.PARAMETER SkipPortable
  只产出安装包，不打 zip。

.EXAMPLE
  pwsh -File build/build-release.ps1
.EXAMPLE
  pwsh -File build/build-release.ps1 -Variant merged -Arch x64 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [ValidateSet('merged', 'split', 'all')][string]$Variant = 'all',
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$Version = '',
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller,
    [switch]$SkipPortable
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsDir = Join-Path $repoRoot 'artifacts'

# ---- 版本解析：命令行 > Directory.Build.props ----
if ([string]::IsNullOrWhiteSpace($Version)) {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    [xml]$xml = Get-Content -LiteralPath $props
    $Version = $xml.Project.PropertyGroup.VersionPrefix
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw '无法解析版本号，请用 -Version 指定。' }
$Version = $Version.Trim()
Write-Host "== BetterScene 发布构建：version=$Version arch=$Arch variant=$Variant configuration=$Configuration =="

# ---- RID / Platform 映射 ----
switch ($Arch) {
    'x64'   { $rid = 'win-x64';   $platform = 'x64' }
    'arm64' { $rid = 'win-arm64'; $platform = 'ARM64' }
}

# 安装包面向 x64（Inno 的 ArchitecturesAllowed=x64compatible）；arm64 只出便携版。
if ($Arch -ne 'x64' -and -not $SkipInstaller) {
    Write-Host "== $Arch 只产出便携版，跳过安装包 =="
    $SkipInstaller = $true
}

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$variants = if ($Variant -eq 'all') { @('merged', 'split') } else { @($Variant) }

foreach ($v in $variants) {
    $selfContained = if ($v -eq 'merged') { 'true' } else { 'false' }
    Write-Host ''
    Write-Host "==== 变体 $v（SelfContained=$selfContained） ===="

    $publishDir = Join-Path $artifactsDir "publish-$Arch-$v"
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

    # ---- 1. publish ----
    Write-Host '== dotnet publish =='
    $appProj = Join-Path $repoRoot 'src/BetterScene.App/BetterScene.App.csproj'
    & dotnet publish $appProj `
        -c $Configuration `
        -r $rid `
        --self-contained $selfContained `
        -p:BetterSceneSelfContained=$selfContained `
        -p:WindowsAppSDKSelfContained=$selfContained `
        -p:Platform=$platform `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

    $exe = Join-Path $publishDir 'BetterScene.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "publish 产物缺少 BetterScene.exe：$publishDir" }

    # 关键回归：合并版自带运行时，WinUIEdit.dll 被误删会导致所有 TextBox 实例化失败并崩溃。
    # 分离版由系统 Windows App Runtime 提供该文件，不在发布目录，故仅对合并版校验。
    if ($v -eq 'merged' -and -not (Test-Path -LiteralPath (Join-Path $publishDir 'WinUIEdit.dll'))) {
        throw "publish 产物缺少 WinUIEdit.dll：$publishDir（会致 TextBox 崩溃）"
    }

    # 图标资源必须存在，否则托盘和窗口图标都会退化成默认图标。
    foreach ($asset in @('Assets\TrayWhite.ico', 'Assets\TrayBlack.ico', 'Assets\BrandDark.png', 'Assets\BrandLight.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $asset))) {
            throw "publish 产物缺少 $asset：$publishDir"
        }
    }

    # ---- 2. 便携版 zip ----
    if (-not $SkipPortable) {
        Write-Host '== 打包便携版 zip =='
        $zipName = "BetterScene-$Version-$Arch-$v-portable.zip"
        $zipPath = Join-Path $artifactsDir $zipName
        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

        # 解压后是一个自包含文件夹，而不是散落一堆文件。
        $staging = Join-Path $artifactsDir "portable-stage-$Arch-$v"
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        New-Item -ItemType Directory -Force -Path (Join-Path $staging 'BetterScene') | Out-Null
        Copy-Item -Path (Join-Path $publishDir '*') -Destination (Join-Path $staging 'BetterScene') -Recurse -Force
        Compress-Archive -Path (Join-Path $staging 'BetterScene') -DestinationPath $zipPath -CompressionLevel Optimal
        Remove-Item -LiteralPath $staging -Recurse -Force
        Write-Host "   已生成 $zipName（$([math]::Round((Get-Item $zipPath).Length/1MB,1)) MB）"
    }

    # ---- 3. 安装版 ----
    if (-not $SkipInstaller) {
        Write-Host '== 编译安装版（Inno Setup） =='

        $isccCandidates = @(
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe',
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        )
        $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $iscc) {
            throw '未找到 Inno Setup 6 的 ISCC.exe。请安装 Inno Setup 6（winget install JRSoftware.InnoSetup），或加 -SkipInstaller。'
        }

        # 语言包缺失则跳过（installer.iss 用 #ifdef ChineseISL 判定）。
        $isl = Join-Path $PSScriptRoot 'languages\ChineseSimplified.isl'
        # split 版需要安装包引导安装两个运行时。
        $bootstrap = if ($v -eq 'split') { '1' } else { '0' }
        $isccArgs = @(
            "/DAppVersion=$Version",
            "/DSourceDir=$publishDir",
            "/DOutputDir=$artifactsDir",
            "/DVariant=$v",
            "/DBootstrapRuntime=$bootstrap",
            (Join-Path $PSScriptRoot 'installer.iss')
        )
        if (Test-Path -LiteralPath $isl) { $isccArgs = @("/DChineseISL=$isl") + $isccArgs }
        else { Write-Warning '未找到 ChineseSimplified.isl，安装包将只有英文界面。' }

        & $iscc @isccArgs
        if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）" }
    }
}

Write-Host ''
Write-Host '== 完成，产物： =='
Get-ChildItem -LiteralPath $artifactsDir -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
