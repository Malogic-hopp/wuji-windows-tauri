[CmdletBinding()]
param(
    [ValidateRange(5, 60)]
    [int]$RecoveryTimeoutSeconds = 12,

    [ValidateRange(30, 600)]
    [int]$UserActionTimeoutSeconds = 180,

    [ValidateRange(1, 10)]
    [int]$SecondCrashDelaySeconds = 3,

    [string]$ReportDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'WUJI.Smoke')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tauriProcessName = 'quantified-self-windows-tauri.exe'
$bridgeProcessName = 'QuantifiedSelf.Windows.Client.Bridge.exe'
$agentProcessName = 'QuantifiedSelf.Windows.Agent.exe'
$startedAt = Get-Date
$runId = $startedAt.ToString('yyyyMMdd-HHmmss')
$reportPath = Join-Path $ReportDirectory "bridge-recovery-smoke-$runId.md"
$events = [System.Collections.Generic.List[object]]::new()
$reportWritten = $false

function Write-Step {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    Write-Host ''
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Add-SmokeEvent {
    param(
        [Parameter(Mandatory)]
        [string]$Step,

        [Parameter(Mandatory)]
        [ValidateSet('PASS', 'FAIL', 'INFO')]
        [string]$Result,

        [Parameter(Mandatory)]
        [string]$Details
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
                    $tauriProcessName,
                    $bridgeProcessName,
                    $agentProcessName
                )
            })
}

function Get-ProcessById {
    param(
        [Parameter(Mandatory)]
        [uint32]$ProcessId
    )

    Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
}

function Get-BridgeChildren {
    param(
        [Parameter(Mandatory)]
        [uint32]$TauriProcessId
    )

    @(Get-WujiProcesses |
            Where-Object {
                $_.Name -eq $bridgeProcessName -and
                $_.ParentProcessId -eq $TauriProcessId -and
                $_.CommandLine -match '(?i)--channel\s+dev(?:\s|$)'
            })
}

function Test-ExpectedProcess {
    param(
        [Parameter(Mandatory)]
        [uint32]$ProcessId,

        [Parameter(Mandatory)]
        [string]$ExpectedName
    )

    $process = Get-ProcessById -ProcessId $ProcessId
    return $null -ne $process -and $process.Name -eq $ExpectedName
}

function Wait-ForValue {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Probe,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,

        [Parameter(Mandatory)]
        [string]$FailureMessage
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
        [Parameter(Mandatory)]
        [uint32]$ProcessId,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if ($null -eq (Get-ProcessById -ProcessId $ProcessId)) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw $FailureMessage
}

function Confirm-ManualCheck {
    param(
        [Parameter(Mandatory)]
        [string]$Step,

        [Parameter(Mandatory)]
        [string]$Prompt
    )

    $answer = Read-Host "$Prompt`n输入 Y 确认，输入其他内容终止"
    if ($answer -notmatch '^(?i:y|yes|是)$') {
        throw "人工检查未通过：$Step"
    }

    Add-SmokeEvent -Step $Step -Result PASS -Details '人工确认通过'
}

function Stop-ValidatedBridge {
    param(
        [Parameter(Mandatory)]
        [uint32]$BridgeProcessId,

        [Parameter(Mandatory)]
        [uint32]$TauriProcessId
    )

    $bridge = Get-ProcessById -ProcessId $BridgeProcessId
    if ($null -eq $bridge) {
        throw "Bridge PID $BridgeProcessId 已不存在"
    }

    if ($bridge.Name -ne $bridgeProcessName -or
        $bridge.ParentProcessId -ne $TauriProcessId -or
        $bridge.CommandLine -notmatch '(?i)--channel\s+dev(?:\s|$)') {
        throw "PID $BridgeProcessId 未通过 dev Bridge 身份复核，拒绝结束进程"
    }

    Stop-Process -Id $BridgeProcessId -Force -ErrorAction Stop
    Wait-ForProcessExit -ProcessId $BridgeProcessId -TimeoutSeconds 5 `
        -FailureMessage "Bridge PID $BridgeProcessId 在结束后仍然存在"
}

function Write-SmokeReport {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('PASS', 'FAIL')]
        [string]$Outcome,

        [string]$Failure = ''
    )

    if ($script:reportWritten) {
        return
    }

    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Tauri BridgeSupervisor 恢复 smoke')
    $lines.Add('')
    $lines.Add("- 开始时间：$($startedAt.ToString('yyyy-MM-dd HH:mm:ss zzz'))")
    $lines.Add("- 结束时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))")
    $lines.Add("- 结果：$Outcome")
    $lines.Add('- 通道：dev')
    if ($Failure) {
        $lines.Add("- 失败原因：$Failure")
    }

    $lines.Add('')
    $lines.Add('| 时间 | 检查项 | 结果 | 详情 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($event in $events) {
        $safeStep = $event.Step.Replace('|', '\|')
        $safeDetails = $event.Details.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
        $lines.Add("| $($event.Time.ToString('HH:mm:ss.fff')) | $safeStep | $($event.Result) | $safeDetails |")
    }

    $lines.Add('')
    $lines.Add('> 本报告只记录进程与人工确认结果，不包含本地路径、原始窗口标题或生产通道数据。')

    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    $script:reportWritten = $true

    Write-Host ''
    Write-Host "报告已写入：$reportPath" -ForegroundColor Yellow
}

try {
    Write-Step '1/8 检查当前 Tauri、Bridge 与 dev 通道'

    $tauriProcesses = @(Get-WujiProcesses | Where-Object Name -eq $tauriProcessName)
    if ($tauriProcesses.Count -ne 1) {
        throw "需要且只能有一个 Tauri dev 进程；当前数量：$($tauriProcesses.Count)"
    }

    $tauri = $tauriProcesses[0]
    $bridgeProcesses = @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId)
    if ($bridgeProcesses.Count -ne 1) {
        throw "当前 Tauri 必须有且只能有一个 --channel dev Bridge；当前数量：$($bridgeProcesses.Count)"
    }

    $bridge1 = $bridgeProcesses[0]
    Add-SmokeEvent -Step '初始 Tauri' -Result PASS `
        -Details "PID=$($tauri.ProcessId)"
    Add-SmokeEvent -Step '初始 dev Bridge' -Result PASS `
        -Details "PID=$($bridge1.ProcessId)，ParentPID=$($bridge1.ParentProcessId)"

    Write-Step '2/8 确认 dev Agent 正在运行'

    $agentProcesses = @(Get-WujiProcesses | Where-Object Name -eq $agentProcessName)
    if ($agentProcesses.Count -eq 0) {
        Read-Host '请在 Tauri UI 点击“启动”，看到“正在记录”后按 Enter'
        $agent = Wait-ForValue -TimeoutSeconds $UserActionTimeoutSeconds `
            -FailureMessage '等待 Agent 启动超时' `
            -Probe {
                $agents = @(Get-WujiProcesses | Where-Object Name -eq $agentProcessName)
                if ($agents.Count -eq 1) { $agents[0] }
            }
    }
    elseif ($agentProcesses.Count -eq 1) {
        $agent = $agentProcesses[0]
    }
    else {
        throw "检测到多个 Agent，无法安全确定 dev Agent；当前数量：$($agentProcesses.Count)"
    }

    $devPipes = @(Get-ChildItem -Path '\\.\pipe\' -ErrorAction Stop |
            Where-Object Name -Like 'QuantifiedSelf.Windows.Agent.dev.*')
    if ($devPipes.Count -eq 0) {
        throw '未发现 QuantifiedSelf.Windows.Agent.dev.* 命名管道，拒绝继续'
    }

    Confirm-ManualCheck -Step 'Agent 初始状态' `
        -Prompt '确认 UI 显示 DEV、正在记录、服务响应正常'
    Add-SmokeEvent -Step 'dev Agent 身份' -Result PASS `
        -Details "PID=$($agent.ProcessId)，dev Pipe 数量=$($devPipes.Count)"

    Write-Step '3/8 自动注入第一次 Bridge 崩溃并等待恢复'
    Write-Host '接下来会自动完成两次 Bridge 结束。请观察 UI；不要点击停止，也不要关闭窗口。' -ForegroundColor Yellow

    $bridge1Pid = [uint32]$bridge1.ProcessId
    Stop-ValidatedBridge -BridgeProcessId $bridge1Pid -TauriProcessId $tauri.ProcessId
    Add-SmokeEvent -Step '第一次结束 Bridge' -Result INFO -Details "Bridge1 PID=$bridge1Pid"

    $bridge2 = Wait-ForValue -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage '第一次崩溃后没有在限定时间内自动创建 Bridge2' `
        -Probe {
            @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId |
                    Where-Object ProcessId -ne $bridge1Pid |
                    Select-Object -First 1)
        }

    if (-not (Test-ExpectedProcess -ProcessId $agent.ProcessId -ExpectedName $agentProcessName)) {
        throw '第一次 Bridge 崩溃后 Agent PID 不再运行'
    }

    Add-SmokeEvent -Step '首次自动恢复' -Result PASS `
        -Details "Bridge1 PID=$bridge1Pid -> Bridge2 PID=$($bridge2.ProcessId)；Agent PID=$($agent.ProcessId) 保持运行"

    Write-Host "Bridge2 已就绪；$SecondCrashDelaySeconds 秒后自动注入第二次崩溃。" -ForegroundColor Yellow
    Start-Sleep -Seconds $SecondCrashDelaySeconds

    Write-Step '4/8 在稳定窗口内注入第二次 Bridge 崩溃'

    $bridge2Pid = [uint32]$bridge2.ProcessId
    Stop-ValidatedBridge -BridgeProcessId $bridge2Pid -TauriProcessId $tauri.ProcessId
    Add-SmokeEvent -Step '第二次结束 Bridge' -Result INFO -Details "Bridge2 PID=$bridge2Pid"
    Start-Sleep -Seconds 2

    $bridgeAfterSecondCrash = @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId)
    if ($bridgeAfterSecondCrash.Count -ne 0) {
        throw "第二次崩溃后仍发现 $($bridgeAfterSecondCrash.Count) 个 Bridge，未进入预期熔断状态"
    }

    if (-not (Test-ExpectedProcess -ProcessId $agent.ProcessId -ExpectedName $agentProcessName)) {
        throw '第二次 Bridge 崩溃后 Agent PID 不再运行'
    }

    Add-SmokeEvent -Step '连续崩溃熔断' -Result PASS `
        -Details "Bridge 数量=0；Agent PID=$($agent.ProcessId) 保持运行"
    Confirm-ManualCheck -Step '熔断 UI' `
        -Prompt '请截图，并确认 UI 显示连接已断开/重新连接，且说明 Agent 保持原状态'

    Write-Step '5/8 手动重新连接'
    Read-Host '请点击 UI 的“重新连接”，恢复后按 Enter'

    $bridge3 = Wait-ForValue -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage '点击重新连接后没有发现 Bridge3' `
        -Probe {
            @(Get-BridgeChildren -TauriProcessId $tauri.ProcessId |
                    Where-Object ProcessId -notin @($bridge1Pid, $bridge2Pid) |
                    Select-Object -First 1)
        }

    if (-not (Test-ExpectedProcess -ProcessId $agent.ProcessId -ExpectedName $agentProcessName)) {
        throw '手动重新连接后 Agent PID 不再运行'
    }

    Add-SmokeEvent -Step '手动重新连接' -Result PASS `
        -Details "Bridge3 PID=$($bridge3.ProcessId)；Agent PID=$($agent.ProcessId) 保持运行"
    Confirm-ManualCheck -Step '重连 UI' `
        -Prompt '确认 UI 恢复“服务响应正常”，Agent 仍显示“正在记录”'

    Write-Step '6/8 正常退出 UI，验证 Agent 独立存活'
    Read-Host '不要点击“停止”；请用窗口关闭按钮或 Alt+F4 正常退出 Tauri，随后按 Enter'

    Wait-ForProcessExit -ProcessId $tauri.ProcessId -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage '等待 Tauri 正常退出超时'
    Start-Sleep -Seconds 2

    $bridgesAfterUiExit = @(Get-WujiProcesses | Where-Object Name -eq $bridgeProcessName)
    if ($bridgesAfterUiExit.Count -ne 0) {
        throw "Tauri 退出后仍有 $($bridgesAfterUiExit.Count) 个 Bridge"
    }

    if (-not (Test-ExpectedProcess -ProcessId $agent.ProcessId -ExpectedName $agentProcessName)) {
        throw 'Tauri 退出后 Agent 没有保持运行'
    }

    Add-SmokeEvent -Step 'UI 退出不停止 Agent' -Result PASS `
        -Details "Tauri/Bridge 已退出；Agent PID=$($agent.ProcessId) 保持运行"

    Write-Step '7/8 重新打开 UI并显式停止 Agent'
    Read-Host '请回到开发终端重新执行 pnpm tauri dev，窗口出现后按 Enter'

    $tauri2 = Wait-ForValue -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage '等待重新启动 Tauri 超时' `
        -Probe {
            @(Get-WujiProcesses |
                    Where-Object {
                        $_.Name -eq $tauriProcessName -and
                        $_.ProcessId -ne $tauri.ProcessId
                    } |
                    Select-Object -First 1)
        }

    $bridge4 = Wait-ForValue -TimeoutSeconds $RecoveryTimeoutSeconds `
        -FailureMessage '重新启动 Tauri 后没有发现 dev Bridge' `
        -Probe {
            @(Get-BridgeChildren -TauriProcessId $tauri2.ProcessId | Select-Object -First 1)
        }

    if (-not (Test-ExpectedProcess -ProcessId $agent.ProcessId -ExpectedName $agentProcessName)) {
        throw '重新打开 UI 前 Agent 已退出'
    }

    Add-SmokeEvent -Step '重新打开 UI' -Result PASS `
        -Details "Tauri PID=$($tauri2.ProcessId)，Bridge PID=$($bridge4.ProcessId)，原 Agent PID=$($agent.ProcessId)"
    Confirm-ManualCheck -Step '重开 UI 状态恢复' `
        -Prompt '确认重新打开后 UI 仍识别到“正在记录”的 Agent'

    Read-Host '现在点击 UI 的“停止”，状态变为“未运行”后按 Enter'
    Wait-ForProcessExit -ProcessId $agent.ProcessId -TimeoutSeconds 30 `
        -FailureMessage '显式停止后 Agent 在 30 秒内仍未退出'
    Add-SmokeEvent -Step '显式停止 Agent' -Result PASS `
        -Details "Agent PID=$($agent.ProcessId) 已退出"

    Write-Step '8/8 最终清理检查'
    Read-Host '请正常关闭 Tauri 窗口，关闭后按 Enter'
    Wait-ForProcessExit -ProcessId $tauri2.ProcessId -TimeoutSeconds $UserActionTimeoutSeconds `
        -FailureMessage '最终等待 Tauri 退出超时'
    Start-Sleep -Seconds 2

    $remaining = @(Get-WujiProcesses)
    if ($remaining.Count -ne 0) {
        $summary = ($remaining | ForEach-Object { "$($_.Name):$($_.ProcessId)" }) -join ', '
        throw "最终仍有相关进程：$summary"
    }

    Add-SmokeEvent -Step '最终进程清理' -Result PASS `
        -Details 'Tauri=0，Bridge=0，Agent=0'
    Write-SmokeReport -Outcome PASS

    Write-Host ''
    Write-Host '阶段 3 BridgeSupervisor 收尾 smoke：通过' -ForegroundColor Green
    Write-Host "复制报告：Get-Content -Encoding UTF8 '$reportPath'" -ForegroundColor Yellow
}
catch {
    $message = $_.Exception.Message
    Add-SmokeEvent -Step 'Smoke 中止' -Result FAIL -Details $message
    Write-SmokeReport -Outcome FAIL -Failure $message
    Write-Error $message
    exit 1
}
