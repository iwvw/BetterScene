<#
.SYNOPSIS
  生成 BetterScene 的 GitHub Release 说明正文。

.DESCRIPTION
  正文 = 变更条目 + 下载表 + 注意事项。变更条目优先取 .github/release-notes/<version>.md；
  没有则回退到与上一个 tag 的 compare 链接。模板按版本号自动选择：
    - 正式发布版（x.y.0）  -> release-notes-template-formal.md（含完整说明与注意事项）
    - 小版本（x.y.z, z>0） -> release-notes-template-patch.md（精简）
  下载表中的大小从 artifacts 目录实测得出，缺失的文件显示 —。

.PARAMETER Version
  版本号，不带 v 前缀，如 0.1.0。

.PARAMETER ArtifactsDir
  下载产物所在目录（含 BetterScene-<version>-*.zip / *.exe）。

.PARAMETER OutputPath
  生成文件路径。

.PARAMETER PreviousTag
  上一个 tag（如 v0.1.0），用于无变更文件时的 compare 链接。

.PARAMETER Kind
  auto（默认，按版本号推断）/ formal / patch。

.EXAMPLE
  pwsh -File .github/scripts/gen-release-notes.ps1 -Version 0.2.0 -ArtifactsDir artifacts -OutputPath release-notes.md -PreviousTag v0.1.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ArtifactsDir,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$PreviousTag = "",
    [ValidateSet('auto', 'formal', 'patch')][string]$Kind = 'auto',
    [string]$Repo = 'iwvw/BetterScene'
)

$ErrorActionPreference = 'Stop'

function Format-Size([string]$fileName) {
    $path = Join-Path $ArtifactsDir $fileName
    if (-not (Test-Path -LiteralPath $path)) { return '—' }
    $mb = (Get-Item -LiteralPath $path).Length / 1MB
    return ('{0:N1} MB' -f $mb)
}

# ---- 选择模板：正式版 = 补丁位为 0（x.y.0），其余为小版本 ----
if ($Kind -eq 'auto') {
    $parts = $Version.Split('.')
    $patch = if ($parts.Length -ge 3) { [int]$parts[2] } else { 0 }
    $Kind = if ($patch -eq 0) { 'formal' } else { 'patch' }
}
$templatePath = Join-Path $PSScriptRoot "../release-notes-template-$Kind.md"
if (-not (Test-Path -LiteralPath $templatePath)) { throw "找不到模板：$templatePath" }

# ---- 变更条目 ----
$changesFile = Join-Path $PSScriptRoot "../release-notes/$Version.md"
if (Test-Path -LiteralPath $changesFile) {
    $changes = (Get-Content -LiteralPath $changesFile -Raw).Trim()
}
elseif (-not [string]::IsNullOrWhiteSpace($PreviousTag)) {
    $changes = "**完整变更**：https://github.com/$Repo/compare/$PreviousTag...v$Version"
}
else {
    $changes = "**完整变更**：https://github.com/$Repo/releases/tag/v$Version"
}

# ---- 填充模板 ----
$template = Get-Content -LiteralPath $templatePath -Raw
$body = $template.Replace('{{CHANGES}}', $changes).Replace('{{VERSION}}', $Version)

$sizeMap = [ordered]@{
    '{{SIZE_X64_MERGED_SETUP}}'      = Format-Size "BetterScene-$Version-x64-merged-setup.exe"
    '{{SIZE_X64_MERGED_PORTABLE}}'   = Format-Size "BetterScene-$Version-x64-merged-portable.zip"
    '{{SIZE_X64_SPLIT_SETUP}}'       = Format-Size "BetterScene-$Version-x64-split-setup.exe"
    '{{SIZE_X64_SPLIT_PORTABLE}}'    = Format-Size "BetterScene-$Version-x64-split-portable.zip"
    '{{SIZE_ARM64_MERGED_PORTABLE}}' = Format-Size "BetterScene-$Version-arm64-merged-portable.zip"
    '{{SIZE_ARM64_SPLIT_PORTABLE}}'  = Format-Size "BetterScene-$Version-arm64-split-portable.zip"
}
foreach ($kv in $sizeMap.GetEnumerator()) {
    $body = $body.Replace($kv.Key, $kv.Value)
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputPath, $body, $utf8)
Write-Host "Release notes written to $OutputPath (kind=$Kind)"
