<#
.SYNOPSIS
  电源场景：拔电切省电（60Hz + 低亮度），插电还原。

.DESCRIPTION
  演示用外部脚本实现「智能场景」，不需要程序内置规则引擎。

  还原用的是「进入前快照」而不是写死数值：拔电前你可能是 78% 亮度、
  165Hz，插电后应该回到 78% / 165Hz，而不是被顶到 100% / 240Hz。
  所以进入时先把当前状态记下来，退出时按快照写回。

.PARAMETER Install
  注册为计划任务，登录后常驻监听电源事件。

.PARAMETER Uninstall
  移除计划任务。

.PARAMETER Watch
  常驻监听电源事件（计划任务用的就是这个入口）。不加任何开关时等同于 -Watch。

.EXAMPLE
  pwsh -File scripts\power-scene.ps1 -Once
.EXAMPLE
  pwsh -File scripts\power-scene.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch]$Once,
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Watch,
    [string]$Ctl = '',
    [int]$BatteryBrightness = 35,
    [int]$BatteryHz = 60
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Ctl)) {
    $Ctl = Join-Path $PSScriptRoot '..\src\BetterScene.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\bsctl.exe'
}
$Ctl = [System.IO.Path]::GetFullPath($Ctl)

$TaskName = 'BetterScene.PowerScene'
$SnapshotFile = Join-Path $env:LOCALAPPDATA 'BetterScene\power-scene-snapshot.json'

function Invoke-Ctl {
    param([string[]]$Arguments)

    $output = & $Ctl @Arguments --json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "bsctl $($Arguments -join ' ') 失败：$output"
    }

    return $output | ConvertFrom-Json
}

function Get-PowerState {
    $status = [System.Windows.Forms.SystemInformation]::PowerStatus
    return $status.PowerLineStatus
}

function Save-Snapshot {
    $state = Invoke-Ctl @('state')

    $snapshot = @{
        brightness = @{}
        refresh    = @{}
    }

    foreach ($display in $state.displays) {
        if ($display.backend -eq 'none') { continue }
        $snapshot.brightness[$display.id] = $display.brightness
        $snapshot.refresh[$display.id] = $display.refreshRate
    }

    $dir = Split-Path -Parent $SnapshotFile
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $snapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $SnapshotFile -Encoding UTF8
    Write-Host "已保存快照：$SnapshotFile"
}

function Restore-Snapshot {
    if (-not (Test-Path $SnapshotFile)) {
        Write-Host '没有快照，跳过还原'
        return
    }

    $snapshot = Get-Content -LiteralPath $SnapshotFile -Raw | ConvertFrom-Json

    foreach ($entry in $snapshot.refresh.PSObject.Properties) {
        try {
            Invoke-Ctl @('display', 'refresh', 'set', '--display', $entry.Name, '--hz', $entry.Value) | Out-Null
            Write-Host "  刷新率还原 $($entry.Name) -> $($entry.Value) Hz"
        }
        catch {
            Write-Warning "刷新率还原失败 $($entry.Name)：$($_.Exception.Message)"
        }
    }

    foreach ($entry in $snapshot.brightness.PSObject.Properties) {
        try {
            Invoke-Ctl @('brightness', 'set', '--display', $entry.Name, '--value', $entry.Value) | Out-Null
            Write-Host "  亮度还原 $($entry.Name) -> $($entry.Value)%"
        }
        catch {
            Write-Warning "亮度还原失败 $($entry.Name)：$($_.Exception.Message)"
        }
    }
}

function Enter-BatteryScene {
    Write-Host '== 拔电：保存快照并切到省电 =='

    Save-Snapshot

    Invoke-Ctl @('display', 'refresh', 'set', '--display', '*', '--hz', $BatteryHz) | Out-Null
    Invoke-Ctl @('brightness', 'set', '--display', '*', '--value', $BatteryBrightness) | Out-Null

    Write-Host "  已切到 $BatteryHz Hz / $BatteryBrightness%"
}

function Exit-BatteryScene {
    Write-Host '== 插电：还原快照 =='
    Restore-Snapshot
}

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "已移除计划任务 $TaskName"
    return
}

if ($Install) {
    $script = $PSCommandPath
    $action = New-ScheduledTaskAction -Execute 'pwsh.exe' `
        -Argument "-NoProfile -WindowStyle Hidden -File `"$script`" -Watch -Ctl `"$Ctl`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Description 'BetterScene 电源场景（拔电省电，插电还原）' -Force | Out-Null

    Write-Host "已注册计划任务 $TaskName（登录时启动）"
    return
}

if ($Once) {
    if (Get-PowerState -eq 'Offline') { Enter-BatteryScene } else { Exit-BatteryScene }
    return
}

Write-Host '监听电源事件，Ctrl+C 退出'
Write-Host "  bsctl: $Ctl"

$last = Get-PowerState
Write-Host "  当前电源：$last"

while ($true) {
    Start-Sleep -Seconds 2

    $now = Get-PowerState
    if ($now -eq $last) { continue }

    $last = $now

    try {
        if ($now -eq 'Offline') { Enter-BatteryScene } else { Exit-BatteryScene }
    }
    catch {
        Write-Warning "场景切换失败：$($_.Exception.Message)"
    }
}
