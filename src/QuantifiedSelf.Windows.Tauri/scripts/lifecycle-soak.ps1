[CmdletBinding()]
param(
    [ValidateRange(3, 1440)]
    [int]$DurationMinutes = 1440,

    [ValidateRange(5, 60)]
    [int]$SampleIntervalSeconds = 60,

    [ValidateRange(0, 60)]
    [int]$WarmupMinutes = 5,

    [ValidateSet('Running', 'Optional', 'NotRunning')]
    [string]$AgentExpectation = 'Running',

    [ValidateRange(16, 1024)]
    [int]$MaxMemoryGrowthMiB = 192,

    [ValidateRange(20, 2000)]
    [int]$MaxHandleGrowth = 200,

    [ValidateRange(2, 20)]
    [double]$RefreshRateMultiplierLimit = 4,

    [ValidateRange(60, 10000)]
    [double]$MinRefreshStormOperationsPerMinute = 240,

    [string]$ReportDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'WUJI.Soak')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tauriProcessName = 'quantified-self-windows-tauri.exe'
$bridgeProcessName = 'QuantifiedSelf.Windows.Client.Bridge.exe'
$agentProcessName = 'QuantifiedSelf.Windows.Agent.exe'
$startedAt = Get-Date
$runId = $startedAt.ToString('yyyyMMdd-HHmmss')
$reportPath = Join-Path $ReportDirectory "lifecycle-soak-$runId.md"
$samplePath = Join-Path $ReportDirectory "lifecycle-soak-$runId.csv"
$samples = [System.Collections.Generic.List[object]]::new()
$checks = [System.Collections.Generic.List[object]]::new()
$outcome = 'FAIL'
$failure = ''

function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'INFO')][string]$Result,
        [Parameter(Mandatory)][string]$Details
    )
    $checks.Add([pscustomobject]@{
            Time = Get-Date
            Name = $Name
            Result = $Result
            Details = $Details
        })
    $color = switch ($Result) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'DarkGray' }
    }
    Write-Host "[$Result] $Name - $Details" -ForegroundColor $color
}

function Get-WujiProcesses {
    @(Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object {
                $_.Name -in @($tauriProcessName, $bridgeProcessName, $agentProcessName)
            })
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

function Get-ProdPipeCount {
    $pipes = @(Get-ChildItem -Path '\\.\pipe\' -ErrorAction Stop |
            Where-Object Name -Like 'QuantifiedSelf.Windows.Agent.*')
    @($pipes | Where-Object Name -NotLike 'QuantifiedSelf.Windows.Agent.dev.*').Count
}

function Get-ValidatedTopology {
    $processes = @(Get-WujiProcesses)
    $tauri = @($processes | Where-Object Name -eq $tauriProcessName)
    if ($tauri.Count -ne 1) {
        throw "需要且只能有一个 Tauri dev 进程；当前数量=$($tauri.Count)"
    }

    $devBridges = @($processes |
            Where-Object {
                $_.Name -eq $bridgeProcessName -and
                $_.CommandLine -match '(?i)--channel\s+dev(?:\s|$)'
            })
    $bridge = @($devBridges | Where-Object ParentProcessId -eq $tauri[0].ProcessId)
    if ($devBridges.Count -ne 1 -or $bridge.Count -ne 1) {
        throw "dev Bridge 拓扑异常；总数=$($devBridges.Count)，当前 Tauri 子进程数=$($bridge.Count)"
    }

    $agents = @(Get-DevAgents)
    if ($agents.Count -gt 1) {
        throw "检测到重复 dev Agent；当前数量=$($agents.Count)"
    }
    if ($AgentExpectation -eq 'Running' -and $agents.Count -ne 1) {
        throw '本次 soak 要求 Agent 正在运行，但未发现唯一 dev Agent'
    }
    if ($AgentExpectation -eq 'NotRunning' -and $agents.Count -ne 0) {
        throw '本次 soak 要求 Agent 未运行，但发现了 dev Agent'
    }

    [pscustomobject]@{
        Tauri = $tauri[0]
        Bridge = $bridge[0]
        Agent = if ($agents.Count -eq 1) { $agents[0] } else { $null }
    }
}

function Get-ResourceSample {
    param(
        [Parameter(Mandatory)][string]$Role,
        [Parameter(Mandatory)][uint32]$ProcessId,
        [Parameter(Mandatory)][datetime]$Timestamp
    )
    $managed = Get-Process -Id $ProcessId -ErrorAction Stop
    $managed.Refresh()
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
    $cpuSeconds = if ($null -eq $managed.CPU) { 0 } else { $managed.CPU }
    $readOperations = if ($null -eq $cim.ReadOperationCount) { 0 } else { $cim.ReadOperationCount }
    $writeOperations = if ($null -eq $cim.WriteOperationCount) { 0 } else { $cim.WriteOperationCount }
    [pscustomobject]@{
        Timestamp = $Timestamp
        ElapsedMinutes = [math]::Round(($Timestamp - $startedAt).TotalMinutes, 3)
        Role = $Role
        ProcessId = $ProcessId
        WorkingSetMiB = [math]::Round($managed.WorkingSet64 / 1MB, 2)
        PrivateMemoryMiB = [math]::Round($managed.PrivateMemorySize64 / 1MB, 2)
        HandleCount = $managed.HandleCount
        CpuSeconds = [math]::Round($cpuSeconds, 3)
        ReadOperations = [uint64]$readOperations
        WriteOperations = [uint64]$writeOperations
    }
}

function Save-Checkpoint {
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    $samples | Export-Csv -LiteralPath $samplePath -NoTypeInformation -Encoding UTF8
}

function Get-Median {
    param([Parameter(Mandatory)][double[]]$Values)
    if ($Values.Count -eq 0) { return 0 }
    $sorted = @($Values | Sort-Object)
    $middle = [math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2
}

function Add-GrowthChecks {
    param([Parameter(Mandatory)][string]$Role)
    $roleSamples = @($samples |
            Where-Object {
                $_.Role -eq $Role -and $_.ElapsedMinutes -ge $WarmupMinutes
            } |
            Sort-Object Timestamp)
    if ($roleSamples.Count -lt 2) {
        $roleSamples = @($samples | Where-Object Role -eq $Role | Sort-Object Timestamp)
    }
    if ($roleSamples.Count -lt 2) {
        throw "$Role 有效资源样本不足"
    }

    $first = $roleSamples[0]
    $last = $roleSamples[-1]
    $memoryGrowth = [math]::Round($last.PrivateMemoryMiB - $first.PrivateMemoryMiB, 2)
    $handleGrowth = $last.HandleCount - $first.HandleCount
    $peakMemory = ($roleSamples | Measure-Object PrivateMemoryMiB -Maximum).Maximum
    $peakHandles = ($roleSamples | Measure-Object HandleCount -Maximum).Maximum

    if ($memoryGrowth -gt $MaxMemoryGrowthMiB) {
        throw "$Role private memory 增长 ${memoryGrowth}MiB，超过阈值 ${MaxMemoryGrowthMiB}MiB"
    }
    if ($handleGrowth -gt $MaxHandleGrowth) {
        throw "$Role handle 增长 $handleGrowth，超过阈值 $MaxHandleGrowth"
    }
    Add-Check -Name "$Role 资源增长" -Result PASS `
        -Details "private memory 增长=${memoryGrowth}MiB，handle 增长=$handleGrowth，峰值=${peakMemory}MiB/$peakHandles handles"
}

function Add-RefreshRateCheck {
    $bridgeSamples = @($samples |
            Where-Object {
                $_.Role -eq 'Bridge' -and $_.ElapsedMinutes -ge $WarmupMinutes
            } |
            Sort-Object Timestamp)
    if ($bridgeSamples.Count -lt 3) {
        $bridgeSamples = @($samples | Where-Object Role -eq 'Bridge' | Sort-Object Timestamp)
    }
    if ($bridgeSamples.Count -lt 3) {
        throw 'Bridge I/O 样本不足，无法检查刷新风暴'
    }

    $rates = [System.Collections.Generic.List[double]]::new()
    for ($index = 1; $index -lt $bridgeSamples.Count; $index++) {
        $previous = $bridgeSamples[$index - 1]
        $current = $bridgeSamples[$index]
        $minutes = ($current.Timestamp - $previous.Timestamp).TotalMinutes
        if ($minutes -le 0) { continue }
        $previousOperations = [double]$previous.ReadOperations + [double]$previous.WriteOperations
        $currentOperations = [double]$current.ReadOperations + [double]$current.WriteOperations
        $delta = [math]::Max(0, $currentOperations - $previousOperations)
        $rates.Add($delta / $minutes)
    }
    if ($rates.Count -eq 0) { throw 'Bridge I/O 采样间隔无效' }

    $median = Get-Median -Values $rates.ToArray()
    $maximum = ($rates | Measure-Object -Maximum).Maximum
    $stormThreshold = [math]::Max(
        $MinRefreshStormOperationsPerMinute,
        $median * $RefreshRateMultiplierLimit)
    if ($maximum -gt $stormThreshold) {
        throw "Bridge I/O 峰值 $([math]::Round($maximum, 2)) ops/min 超过稳定速率门禁 $([math]::Round($stormThreshold, 2)) ops/min"
    }
    Add-Check -Name '刷新频率稳定性' -Result PASS `
        -Details "Bridge I/O 中位数=$([math]::Round($median, 2)) ops/min，峰值=$([math]::Round($maximum, 2)) ops/min，未发现倍增型刷新风暴"
}

function Write-SoakReport {
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Tauri dev 生命周期 soak 报告')
    $lines.Add('')
    $lines.Add("- 开始时间：$($startedAt.ToString('yyyy-MM-dd HH:mm:ss zzz'))")
    $lines.Add("- 结束时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))")
    $lines.Add("- 计划时长：$DurationMinutes 分钟")
    $lines.Add("- 实际时长：$([math]::Round(((Get-Date) - $startedAt).TotalMinutes, 2)) 分钟")
    $lines.Add("- 采样间隔：$SampleIntervalSeconds 秒")
    $lines.Add("- Agent 预期：$AgentExpectation")
    $lines.Add("- 结果：$outcome")
    if ($failure) { $lines.Add("- 失败原因：$failure") }
    $lines.Add('')
    $lines.Add('| 时间 | 检查项 | 结果 | 详情 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($check in $checks) {
        $safeName = $check.Name.Replace('|', '\|')
        $safeDetails = $check.Details.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
        $lines.Add("| $($check.Time.ToString('HH:mm:ss')) | $safeName | $($check.Result) | $safeDetails |")
    }
    $lines.Add('')
    $lines.Add("原始资源样本：``$([System.IO.Path]::GetFileName($samplePath))``")
    $lines.Add('')
    $lines.Add('> 本工具只读采样已运行的 dev 进程，不启动、停止或重启 Tauri、Bridge、Agent，不读取数据库、设置内容、窗口标题、路径或生产通道数据。')
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    Write-Host ''
    Write-Host "报告已写入：$reportPath" -ForegroundColor Yellow
    Write-Host "资源样本已写入：$samplePath" -ForegroundColor Yellow
}

try {
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    $topology = Get-ValidatedTopology
    $expectedTauriPid = [uint32]$topology.Tauri.ProcessId
    $expectedBridgePid = [uint32]$topology.Bridge.ProcessId
    $expectedAgentPid = if ($null -ne $topology.Agent) { [uint32]$topology.Agent.ProcessId } else { 0 }
    $prodAgentBaseline = @(Get-ProdAgents).Count
    $prodPipeBaseline = Get-ProdPipeCount

    Add-Check -Name '初始 dev 拓扑' -Result PASS `
        -Details "Tauri=1，Bridge=1，dev Agent=$(@(Get-DevAgents).Count)，无孤儿或重复实例"
    Add-Check -Name 'prod 只读基线' -Result INFO `
        -Details "prod Agent 数量=$prodAgentBaseline，prod pipe 数量=$prodPipeBaseline；仅用于结束时不变量比较"

    $deadline = $startedAt.AddMinutes($DurationMinutes)
    while ((Get-Date) -lt $deadline) {
        $topology = Get-ValidatedTopology
        if ($topology.Tauri.ProcessId -ne $expectedTauriPid) {
            throw "Tauri PID 发生变化：$expectedTauriPid -> $($topology.Tauri.ProcessId)"
        }
        if ($topology.Bridge.ProcessId -ne $expectedBridgePid) {
            throw "Bridge PID 发生变化：$expectedBridgePid -> $($topology.Bridge.ProcessId)"
        }
        if ($expectedAgentPid -ne 0 -and
            ($null -eq $topology.Agent -or $topology.Agent.ProcessId -ne $expectedAgentPid)) {
            throw "dev Agent PID 发生变化或退出：期望 $expectedAgentPid"
        }
        if ($expectedAgentPid -eq 0 -and $null -ne $topology.Agent) {
            throw 'soak 期间意外出现 dev Agent'
        }

        $timestamp = Get-Date
        $samples.Add((Get-ResourceSample -Role Tauri -ProcessId $expectedTauriPid -Timestamp $timestamp))
        $samples.Add((Get-ResourceSample -Role Bridge -ProcessId $expectedBridgePid -Timestamp $timestamp))
        if ($expectedAgentPid -ne 0) {
            $samples.Add((Get-ResourceSample -Role Agent -ProcessId $expectedAgentPid -Timestamp $timestamp))
        }
        Save-Checkpoint

        $latestTauri = @($samples | Where-Object Role -eq 'Tauri')[-1]
        $latestBridge = @($samples | Where-Object Role -eq 'Bridge')[-1]
        Write-Host ("[{0}] elapsed={1:N1}m Tauri={2:N1}MiB/{3}h Bridge={4:N1}MiB/{5}h" -f `
                $timestamp.ToString('HH:mm:ss'),
                ($timestamp - $startedAt).TotalMinutes,
                $latestTauri.PrivateMemoryMiB,
                $latestTauri.HandleCount,
                $latestBridge.PrivateMemoryMiB,
                $latestBridge.HandleCount) -ForegroundColor DarkGray

        $remainingSeconds = ($deadline - (Get-Date)).TotalSeconds
        if ($remainingSeconds -le 0) { break }
        Start-Sleep -Seconds ([math]::Min($SampleIntervalSeconds, [math]::Ceiling($remainingSeconds)))
    }

    Add-GrowthChecks -Role Tauri
    Add-GrowthChecks -Role Bridge
    if ($expectedAgentPid -ne 0) { Add-GrowthChecks -Role Agent }
    Add-RefreshRateCheck

    $finalTopology = Get-ValidatedTopology
    if ($finalTopology.Tauri.ProcessId -ne $expectedTauriPid -or
        $finalTopology.Bridge.ProcessId -ne $expectedBridgePid) {
        throw '结束时 Tauri/Bridge 拓扑与基线不同'
    }
    if (@(Get-ProdAgents).Count -ne $prodAgentBaseline -or
        (Get-ProdPipeCount) -ne $prodPipeBaseline) {
        throw 'prod Agent 或 prod pipe 数量相对只读基线发生变化'
    }
    Add-Check -Name '最终拓扑与 prod 不变量' -Result PASS `
        -Details '无孤儿 Bridge、无重复 Agent；prod Agent/pipe 数量与基线一致'
    $outcome = 'PASS'
}
catch {
    $failure = $_.Exception.Message
    Add-Check -Name 'Soak 中止' -Result FAIL -Details $failure
}
finally {
    if ($samples.Count -gt 0) { Save-Checkpoint }
    Write-SoakReport
}

if ($outcome -ne 'PASS') {
    Write-Error $failure
    exit 1
}

Write-Host ''
Write-Host "阶段 6C Tauri dev soak：通过（$DurationMinutes 分钟）" -ForegroundColor Green
Write-Host "复制报告：Get-Content -Encoding UTF8 '$reportPath'" -ForegroundColor Yellow
