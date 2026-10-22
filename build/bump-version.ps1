<#
.SYNOPSIS
  递增或设置 BetterScene 的版本号（唯一来源：Directory.Build.props 的 VersionPrefix）。

.DESCRIPTION
  -Bump 与 -Set 二选一：
    -Bump patch|minor|major  在当前版本上递增（不提供时默认 patch）
    -Set  x.y.z              直接设定为指定版本
  同时更新 VersionPrefix / Version / AssemblyVersion / FileVersion，
  并打印新版本号；在 GitHub Actions 中同时写入 GITHUB_OUTPUT。

.EXAMPLE
  pwsh -File build/bump-version.ps1 -Bump minor
  pwsh -File build/bump-version.ps1 -Set 0.2.0
#>
[CmdletBinding(DefaultParameterSetName = 'Bump')]
param(
    [Parameter(ParameterSetName = 'Bump')]
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',

    [Parameter(Mandatory = $true, ParameterSetName = 'Set')]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Set
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

[xml]$xml = Get-Content -LiteralPath $propsPath
$node = $xml.Project.PropertyGroup.VersionPrefix
if ($null -eq $node) { throw "Directory.Build.props 缺少 VersionPrefix。" }
$current = $node.InnerText.Trim()
if ($current -notmatch '^\d+\.\d+\.\d+$') { throw "当前版本号格式非法：$current" }

if ($PSCmdlet.ParameterSetName -eq 'Set') {
    $new = $Set
}
else {
    $parts = $current.Split('.') | ForEach-Object { [int]$_ }
    switch ($Bump) {
        'major' { $new = "{0}.0.0" -f ($parts[0] + 1) }
        'minor' { $new = "{0}.{1}.0" -f $parts[0], ($parts[1] + 1) }
        default { $new = "{0}.{1}.{2}" -f $parts[0], $parts[1], ($parts[2] + 1) }
    }
}

$content = Get-Content -LiteralPath $propsPath -Raw

# 四个属性一起改，避免界面显示的版本和程序集版本不一致。
$content = [System.Text.RegularExpressions.Regex]::Replace($content, '(<VersionPrefix>)[^<]*(</VersionPrefix>)', "`${1}$new`${2}", 1)
$content = [System.Text.RegularExpressions.Regex]::Replace($content, '(<Version>)[^<]*(</Version>)', "`${1}$new`${2}", 1)
$content = [System.Text.RegularExpressions.Regex]::Replace($content, '(<AssemblyVersion>)[^<]*(</AssemblyVersion>)', "`${1}$new.0`${2}", 1)
$content = [System.Text.RegularExpressions.Regex]::Replace($content, '(<FileVersion>)[^<]*(</FileVersion>)', "`${1}$new.0`${2}", 1)

Set-Content -LiteralPath $propsPath -Value $content -NoNewline -Encoding UTF8

Write-Host "version: $current -> $new"
if ($env:GITHUB_OUTPUT) {
    "version=$new" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
}
Write-Output $new
