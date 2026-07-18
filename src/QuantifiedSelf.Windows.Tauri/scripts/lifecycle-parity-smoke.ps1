[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int]$CycleCount = 3,

    [ValidateRange(5, 60)]
    [int]$RecoveryTimeoutSeconds = 15,

    [ValidateRange(30, 900)]
    [int]$UserActionTimeoutSeconds = 240,

    [string]$ReportDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'WUJI.Smoke')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$wpfProcessName = 'QuantifiedSelf.Windows.App.exe'
$tauriProcessName = 'quantified-self-windows-tauri.exe'
$bridgeProcessName = 'QuantifiedSelf.Windows.Client.Bridge.exe'
$agentProcessName = 'QuantifiedSelf.Windows.Agent.exe'
$startedAt = Get-Date
$runId = $startedAt.ToString('yyyyMMdd-HHmmss')
$reportPath = Join-Path $ReportDirectory "lifecycle-parity-smoke-$runId.md"
$events = [System.Collections.Generic.List[object]]::new()
$reportWritten = $false

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WujiNativeWindowProbe
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);

    public static IntPtr FindTopLevelWindow(uint expectedProcessId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == expectedProcessId)
            {
                result = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ''
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Add-SmokeEvent {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'INFO')][string]$Result,
        [Parameter(Mandatory)][string]$Details
    )

    $events.Add([pscustomobject]@{
            Time = Get-Date
            Step = $Step
            Result = $Result
            Details = $Details
        })
    $color = switch ($Result) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'DarkGray' }
    }
    Write-Host "[$Result] $Step - $Details" -ForegroundColor $color
}

function Get-WujiProcesses {
    @(Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object {
                $_.Name -in @(
                    $wpfProcessName,
                    $tauriProcessName,
                    $bridgeProcessName,
                    $agentProcessName
                )
            })
}

function Get-WujiProcessById {
    param([Parameter(Mandatory)][uint32]$ProcessId)
    Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
}

function Get-DevAgents {
    @(Get-WujiProcesses |
            Where-Object {
                $_.Name -eq $agentProcessName -and
                $_.CommandLine -match '(?i)--channel\s+dev(?:\s|$)'
            })
}

function Get-ProdAgents {
    @(Get-WujiProcesses |
            Where-Object {
                $_.Name -eq $agentProcessName -and
                $_.CommandLine -notmatch '(?i)--channel\s+dev(?:\s|$)'
            })
}

function Get-AgentPipeInventory {
    $pipes = @(Get-ChildItem -Path '\\.\pipe\' -ErrorAction Stop |
            Where-Object Name -Like 'QuantifiedSelf.Windows.Agent.*')
    [pscustomobject]@{
        Dev = @($pipes | Where-Object Name -Like 'QuantifiedSelf.Windows.Agent.dev.*').Count
        Prod = @($pipes | Where-Object Name -NotLike 'QuantifiedSelf.Windows.Agent.dev.*').Count
    }
}

function Get-BridgeChildren {
    param([Parameter(Mandatory)][uint32]$TauriProcessId)
    @(Get-WujiProcesses |
            Where-Object {
                $_.Name -eq $bridgeProcessName -and
                $_.ParentProcessId -eq $TauriProcessId -and
                $_.CommandLine -match '(?i)--channel\s+dev(?:\s|$)'
            })
}

function Wait-ForValue {
    param(
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $value = & $Probe
        if ($null -ne $value) {
            return $value
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw $FailureMessage
}

function Wait-ForProcessExit {
    param(
        [Parameter(Mandatory)][uint32]$ProcessId,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$FailureMessage
    )
    Wait-ForValue -TimeoutSeconds $TimeoutSeconds -FailureMessage $FailureMessage -Probe {
        if ($null -eq (Get-WujiProcessById -ProcessId $ProcessId)) { $true }
    } | Out-Null
}

function Get-WindowSnapshot {
    param([Parameter(Mandatory)][uint32]$ProcessId)
    $handle = [WujiNativeWindowProbe]::FindTopLevelWindow($ProcessId)
    if ($handle -eq [IntPtr]::Zero) {
        return $null
    }
    [pscustomobject]@{
        Handle = $handle
        Visible = [WujiNativeWindowProbe]::IsWindowVisible($handle)
        Minimized = [WujiNativeWindowProbe]::IsIconic($handle)
    }
}

function Wait-ForWindowState {
    param(
        [Parameter(Mandatory)][uint32]$ProcessId,
        [Parameter(Mandatory)][ValidateSet('Visible', 'Hidden')][string]$State,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$FailureMessage
    )
    Wait-ForValue -TimeoutSeconds $TimeoutSeconds -FailureMessage $FailureMessage -Probe {
        $snapshot = Get-WindowSnapshot -ProcessId $ProcessId
        if ($null -eq $snapshot) { return $null }
        if ($State -eq 'Visible' -and $snapshot.Visible -and -not $snapshot.Minimized) {
            return $snapshot
        }
        if ($State -eq 'Hidden' -and -not $snapshot.Visible) {
            return $snapshot
        }
        return $null
    }
}

function Wait-ForSingleProcess {
    param(
        [Parameter(Mandatory)][string]$Name,
        [uint32]$ExcludeProcessId = 0
    )
    Wait-ForValue -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage "等待 $Name 启动超时" `
        -Probe {
            $matches = @(Get-WujiProcesses |
                    Where-Object {
                        $_.Name -eq $Name -and
                        ($ExcludeProcessId -eq 0 -or $_.ProcessId -ne $ExcludeProcessId)
                    })
            if ($matches.Count -eq 1) { $matches[0] }
        }
}

function Confirm-ManualCheck {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Prompt
    )
    $answer = Read-Host "$Prompt`n输入 Y 确认，输入其他内容终止"
    if ($answer -notmatch '^(?i:y|yes|是)$') {
        throw "人工检查未通过：$Step"
    }
    Add-SmokeEvent -Step $Step -Result PASS -Details '人工确认通过'
}

function Stop-ValidatedDevProcess {
    param(
        [Parameter(Mandatory)][uint32]$ProcessId,
        [Parameter(Mandatory)][ValidateSet('Tauri', 'Bridge')][string]$Kind,
        [uint32]$ExpectedParentProcessId = 0
    )
    $process = Get-WujiProcessById -ProcessId $ProcessId
    if ($null -eq $process) {
        throw "$Kind PID $ProcessId 已不存在"
    }

    if ($Kind -eq 'Tauri') {
        $bridgeChildren = @(Get-BridgeChildren -TauriProcessId $ProcessId)
        if ($process.Name -ne $tauriProcessName -or $bridgeChildren.Count -ne 1) {
            throw "PID $ProcessId 未通过 Tauri dev 身份复核"
        }
    }
    if ($Kind -eq 'Bridge' -and (
            $process.Name -ne $bridgeProcessName -or
            $process.ParentProcessId -ne $ExpectedParentProcessId -or
            $process.CommandLine -notmatch '(?i)--channel\s+dev(?:\s|$)')) {
        throw "PID $ProcessId 未通过 dev Bridge 身份复核"
    }

    Stop-Process -Id $ProcessId -Force -ErrorAction Stop
    Wait-ForProcessExit -ProcessId $ProcessId -TimeoutSeconds 10 `
        -FailureMessage "$Kind PID $ProcessId 在结束后仍然存在"
}

function Assert-AgentIdentity {
    param([Parameter(Mandatory)][uint32]$ExpectedProcessId)
    $agents = @(Get-DevAgents)
    if ($agents.Count -ne 1 -or $agents[0].ProcessId -ne $ExpectedProcessId) {
        throw "dev Agent 身份变化或出现重复实例；期望 PID=$ExpectedProcessId，当前数量=$($agents.Count)"
    }
}

function Assert-NoOrphanBridge {
    $tauriIds = @(Get-WujiProcesses |
            Where-Object Name -eq $tauriProcessName |
            ForEach-Object ProcessId)
    $orphans = @(Get-WujiProcesses |
            Where-Object {
                $_.Name -eq $bridgeProcessName -and
                $_.CommandLine -match '(?i)--channel\s+dev(?:\s|$)' -and
                $_.ParentProcessId -notin $tauriIds
            })
    if ($orphans.Count -ne 0) {
        throw "发现 $($orphans.Count) 个孤儿 dev Bridge"
    }
}

function Write-SmokeReport {
    param(
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL')][string]$Outcome,
        [string]$Failure = ''
    )
    if ($script:reportWritten) { return }
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Tauri/WPF 宿主生命周期 parity smoke')
    $lines.Add('')
    $lines.Add("- 开始时间：$($startedAt.ToString('yyyy-MM-dd HH:mm:ss zzz'))")
    $lines.Add("- 结束时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))")
    $lines.Add("- 结果：$Outcome")
    $lines.Add('- 通道：dev')
    $lines.Add("- 启动/退出循环：$CycleCount 次")
    if ($Failure) { $lines.Add("- 失败原因：$Failure") }
    $lines.Add('')
    $lines.Add('| 时间 | 检查项 | 结果 | 详情 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($event in $events) {
        $safeStep = $event.Step.Replace('|', '\|')
        $safeDetails = $event.Details.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
        $lines.Add("| $($event.Time.ToString('HH:mm:ss.fff')) | $safeStep | $($event.Result) | $safeDetails |")
    }
    $lines.Add('')
    $lines.Add('> 报告只记录 dev 进程临时 PID、数量和人工确认结果，不记录路径、SID、窗口标题、数据库内容或生产通道标识。')
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    $script:reportWritten = $true
    Write-Host ''
    Write-Host "报告已写入：$reportPath" -ForegroundColor Yellow
}

try {
    Write-Step '1/10 安全预检与 prod 基线'
    $unexpectedHosts = @(Get-WujiProcesses |
            Where-Object Name -in @($wpfProcessName, $tauriProcessName, $bridgeProcessName))
    if ($unexpectedHosts.Count -ne 0) {
        throw '开始前必须关闭 WPF、Tauri 和 Bridge；脚本不会自动结束未识别进程'
    }
    if (@(Get-DevAgents).Count -gt 1) {
        throw '开始前已存在多个 dev Agent，拒绝继续'
    }
    $prodAgentBaseline = @(Get-ProdAgents).Count
    $pipeBaseline = Get-AgentPipeInventory
    Add-SmokeEvent -Step '安全预检' -Result PASS `
        -Details "宿主进程=0，dev Agent<=1，prod Agent 基线=$prodAgentBaseline，prod pipe 基线=$($pipeBaseline.Prod)"

    Write-Step '2/10 WPF dev close/minimize-to-tray 对照'
    Write-Host '请在独立终端运行：' -ForegroundColor Yellow
    Write-Host 'dotnet run --project .\src\QuantifiedSelf.Windows.App\QuantifiedSelf.Windows.App.csproj -- --channel dev --ui-preview'
    Read-Host 'WPF dev 窗口出现后按 Enter；脚本将直接验证实际的最小化/关闭到托盘行为'
    $wpf = Wait-ForSingleProcess -Name $wpfProcessName
    if ($wpf.CommandLine -notmatch '(?i)--channel\s+dev(?:\s|$)' -or
        $wpf.CommandLine -notmatch '(?i)--ui-preview(?:\s|$)') {
        throw 'WPF 进程不是 --channel dev --ui-preview，拒绝继续'
    }
    Wait-ForWindowState -ProcessId $wpf.ProcessId -State Visible `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage '未发现可见的 WPF dev 主窗口' | Out-Null

    Read-Host '请点击 WPF 最小化按钮，窗口从任务栏消失后按 Enter'
    Wait-ForWindowState -ProcessId $wpf.ProcessId -State Hidden `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage 'WPF 最小化后未隐藏到托盘' | Out-Null
    Add-SmokeEvent -Step 'WPF minimize-to-tray' -Result PASS -Details '窗口隐藏，WPF 进程保持运行'

    Read-Host '请从 WPF 托盘恢复窗口，窗口可见后按 Enter'
    Wait-ForWindowState -ProcessId $wpf.ProcessId -State Visible `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage 'WPF 托盘恢复失败' | Out-Null
    Read-Host '请点击 WPF 关闭按钮，窗口从任务栏消失后按 Enter'
    Wait-ForWindowState -ProcessId $wpf.ProcessId -State Hidden `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage 'WPF 关闭后未隐藏到托盘' | Out-Null
    Add-SmokeEvent -Step 'WPF close-to-tray' -Result PASS -Details '窗口隐藏，WPF 进程保持运行'

    Read-Host '请通过 WPF 托盘执行真正退出，随后按 Enter'
    Wait-ForProcessExit -ProcessId $wpf.ProcessId -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage 'WPF 托盘退出超时'
    Add-SmokeEvent -Step 'WPF 真正退出' -Result PASS -Details 'WPF 进程已退出'

    Write-Step '3/10 Tauri dev close/minimize-to-tray 对照'
    Write-Host '请在独立终端运行：cd .\src\QuantifiedSelf.Windows.Tauri；pnpm tauri dev' -ForegroundColor Yellow
    Read-Host 'Tauri 窗口出现后按 Enter'
    $tauri = Wait-ForSingleProcess -Name $tauriProcessName
    $bridge = Wait-ForValue -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage 'Tauri 启动后没有唯一 dev Bridge' `
        -Probe {
            $children = @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId)
            if ($children.Count -eq 1) { $children[0] }
        }
    Wait-ForWindowState -ProcessId $tauri.ProcessId -State Visible `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage '未发现可见的 Tauri 主窗口' | Out-Null

    Read-Host '请点击 Tauri 最小化按钮，窗口从任务栏消失后按 Enter'
    Wait-ForWindowState -ProcessId $tauri.ProcessId -State Hidden `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage 'Tauri 最小化后未隐藏到托盘' | Out-Null
    if ($null -eq (Get-WujiProcessById -ProcessId $bridge.ProcessId)) { throw 'Tauri 最小化导致 Bridge 退出' }
    Add-SmokeEvent -Step 'Tauri minimize-to-tray' -Result PASS -Details '窗口隐藏，Tauri/Bridge 保持运行'

    Read-Host '请从 Tauri 托盘恢复窗口，窗口可见后按 Enter'
    Wait-ForWindowState -ProcessId $tauri.ProcessId -State Visible `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage 'Tauri 托盘恢复失败' | Out-Null
    Read-Host '请点击 Tauri 关闭按钮，窗口从任务栏消失后按 Enter'
    Wait-ForWindowState -ProcessId $tauri.ProcessId -State Hidden `
        -TimeoutSeconds $RecoveryTimeoutSeconds -FailureMessage 'Tauri 关闭后未隐藏到托盘' | Out-Null
    if ($null -eq (Get-WujiProcessById -ProcessId $bridge.ProcessId)) { throw 'Tauri close-to-tray 导致 Bridge 退出' }
    Add-SmokeEvent -Step 'Tauri close-to-tray' -Result PASS -Details '窗口隐藏，Tauri/Bridge 保持运行'
    Confirm-ManualCheck -Step 'WPF/Tauri 托盘语义一致' `
        -Prompt '确认 WPF 与 Tauri 均为：最小化/关闭只隐藏，托盘恢复同一窗口，真正退出才结束 UI 宿主'

    Write-Step '4/10 启动或复用唯一 dev Agent'
    Read-Host '请恢复 Tauri；若 Agent 未运行则点击“启动”，看到“正在记录”后按 Enter'
    $agent = Wait-ForValue -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage '等待唯一 dev Agent 超时' `
        -Probe {
            $agents = @(Get-DevAgents)
            if ($agents.Count -eq 1) { $agents[0] }
        }
    $devPipes = Get-AgentPipeInventory
    if ($devPipes.Dev -ne 1) { throw "期望一个 dev Agent pipe，当前数量=$($devPipes.Dev)" }
    Add-SmokeEvent -Step '唯一 dev Agent' -Result PASS -Details "Agent PID=$($agent.ProcessId)，dev pipe=1"

    Write-Step '5/10 UI 正常退出后 Agent 独立存活'
    Read-Host '不要停止 Agent；请通过 Tauri 托盘执行“退出吾迹”，随后按 Enter'
    Wait-ForProcessExit -ProcessId $tauri.ProcessId -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage 'Tauri 正常退出超时'
    Wait-ForProcessExit -ProcessId $bridge.ProcessId -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage 'Tauri 正常退出后 Bridge 未退出'
    Assert-AgentIdentity -ExpectedProcessId $agent.ProcessId
    Assert-NoOrphanBridge
    Add-SmokeEvent -Step 'UI 退出不停止 Agent' -Result PASS `
        -Details "Tauri/Bridge 已退出，Agent PID=$($agent.ProcessId) 保持运行"

    Write-Step '6/10 重启恢复与 Bridge 崩溃恢复'
    Write-Host '请重新执行 pnpm tauri dev。' -ForegroundColor Yellow
    Read-Host '窗口出现后按 Enter'
    $tauri = Wait-ForSingleProcess -Name $tauriProcessName
    $bridge = Wait-ForValue -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage '重启 Tauri 后没有唯一 dev Bridge' `
        -Probe {
            $children = @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId)
            if ($children.Count -eq 1) { $children[0] }
        }
    Assert-AgentIdentity -ExpectedProcessId $agent.ProcessId
    Confirm-ManualCheck -Step 'Tauri 重启状态恢复' -Prompt '确认 UI 识别到原 Agent，状态仍为“正在记录”'

    $bridge1Pid = [uint32]$bridge.ProcessId
    Stop-ValidatedDevProcess -ProcessId $bridge1Pid -Kind Bridge `
        -ExpectedParentProcessId $tauri.ProcessId
    $bridge = Wait-ForValue -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage 'Bridge 崩溃后未自动恢复' `
        -Probe {
            $children = @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId |
                    Where-Object ProcessId -ne $bridge1Pid)
            if ($children.Count -eq 1) { $children[0] }
        }
    Assert-AgentIdentity -ExpectedProcessId $agent.ProcessId
    Add-SmokeEvent -Step 'Bridge 崩溃恢复' -Result PASS `
        -Details "Bridge PID=$bridge1Pid -> $($bridge.ProcessId)，Agent PID 不变"

    Write-Step '7/10 Tauri 崩溃与重新启动恢复'
    $tauriCrashPid = [uint32]$tauri.ProcessId
    $bridgeBeforeCrashPid = [uint32]$bridge.ProcessId
    Stop-ValidatedDevProcess -ProcessId $tauriCrashPid -Kind Tauri
    Wait-ForProcessExit -ProcessId $bridgeBeforeCrashPid -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage 'Tauri 崩溃后 Bridge 未随 stdin EOF 退出'
    Assert-NoOrphanBridge
    Assert-AgentIdentity -ExpectedProcessId $agent.ProcessId
    Add-SmokeEvent -Step 'Tauri 崩溃隔离' -Result PASS `
        -Details "Tauri/Bridge 已退出，Agent PID=$($agent.ProcessId) 保持运行"

    Write-Host '请再次执行 pnpm tauri dev。' -ForegroundColor Yellow
    Read-Host '窗口出现后按 Enter'
    $tauri = Wait-ForSingleProcess -Name $tauriProcessName
    $bridge = Wait-ForValue -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage 'Tauri 崩溃后重启没有唯一 dev Bridge' `
        -Probe {
            $children = @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId)
            if ($children.Count -eq 1) { $children[0] }
        }
    Assert-AgentIdentity -ExpectedProcessId $agent.ProcessId
    Confirm-ManualCheck -Step 'Tauri 崩溃后状态恢复' -Prompt '确认 UI 再次识别原 Agent，且没有启动重复 Agent'

    Write-Step "8/10 执行 $CycleCount 次正常启动/退出循环"
    for ($cycle = 1; $cycle -le $CycleCount; $cycle++) {
        $currentTauriPid = [uint32]$tauri.ProcessId
        $currentBridgePid = [uint32]$bridge.ProcessId
        Read-Host "循环 $cycle/$CycleCount：请通过托盘退出 Tauri，随后按 Enter"
        Wait-ForProcessExit -ProcessId $currentTauriPid -TimeoutSeconds $UserActionTimeoutSeconds `
            -FailureMessage "循环 $cycle：Tauri 退出超时"
        Wait-ForProcessExit -ProcessId $currentBridgePid -TimeoutSeconds $RecoveryTimeoutSeconds `
            -FailureMessage "循环 $cycle：Bridge 未退出"
        Assert-NoOrphanBridge
        Assert-AgentIdentity -ExpectedProcessId $agent.ProcessId

        Write-Host "循环 $cycle/$CycleCount：请重新执行 pnpm tauri dev。" -ForegroundColor Yellow
        Read-Host '窗口出现后按 Enter'
        $tauri = Wait-ForSingleProcess -Name $tauriProcessName -ExcludeProcessId $currentTauriPid
        $bridge = Wait-ForValue -TimeoutSeconds $RecoveryTimeoutSeconds `
            -FailureMessage "循环 $cycle：没有唯一 dev Bridge" `
            -Probe {
                $children = @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId)
                if ($children.Count -eq 1) { $children[0] }
            }
        Assert-AgentIdentity -ExpectedProcessId $agent.ProcessId
        Add-SmokeEvent -Step "启动/退出循环 $cycle" -Result PASS `
            -Details "Tauri/Bridge 完整回收并重建，Agent PID=$($agent.ProcessId) 不变"
    }

    Write-Step '9/10 显式停止 Agent'
    Read-Host '请在 Tauri UI 点击“停止”，状态变为“未运行”后按 Enter'
    Wait-ForProcessExit -ProcessId $agent.ProcessId -TimeoutSeconds 30 `
        -FailureMessage '显式 stop 后 Agent 在 30 秒内仍未退出'
    if (@(Get-DevAgents).Count -ne 0) { throw '显式 stop 后仍存在 dev Agent' }
    Add-SmokeEvent -Step '显式停止 Agent' -Result PASS -Details "Agent PID=$($agent.ProcessId) 已退出"

    Write-Step '10/10 最终清理与 prod 不变量'
    Read-Host '请通过托盘退出最后一个 Tauri 窗口，随后按 Enter'
    Wait-ForProcessExit -ProcessId $tauri.ProcessId -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage '最终 Tauri 退出超时'
    Wait-ForProcessExit -ProcessId $bridge.ProcessId -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage '最终 Bridge 退出超时'
    Assert-NoOrphanBridge
    if (@(Get-DevAgents).Count -ne 0) { throw '最终仍存在 dev Agent' }
    $pipeFinal = Get-AgentPipeInventory
    if (@(Get-ProdAgents).Count -ne $prodAgentBaseline -or $pipeFinal.Prod -ne $pipeBaseline.Prod) {
        throw 'prod Agent 或 prod pipe 数量相对基线发生变化'
    }
    Add-SmokeEvent -Step '最终清理与 prod 隔离' -Result PASS `
        -Details "Tauri=0，Bridge=0，dev Agent=0；prod Agent/pipe 数量与基线一致"

    Write-SmokeReport -Outcome PASS
    Write-Host ''
    Write-Host '阶段 6C 生命周期 parity 与循环恢复 smoke：通过' -ForegroundColor Green
    Write-Host "复制报告：Get-Content -Encoding UTF8 '$reportPath'" -ForegroundColor Yellow
}
catch {
    $message = $_.Exception.Message
    Add-SmokeEvent -Step 'Smoke 中止' -Result FAIL -Details $message
    Write-SmokeReport -Outcome FAIL -Failure $message
    Write-Error $message
    exit 1
}
