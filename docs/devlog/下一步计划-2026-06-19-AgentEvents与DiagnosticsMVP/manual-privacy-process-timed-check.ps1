param(
    [string]$AgentRoot,
    [int]$ReloadWaitSeconds = 5,
    [int]$HoldSeconds = 12
)

$ErrorActionPreference = "Stop"

function Get-AgentRoot {
    param([string]$Value)

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    if (Test-Path -LiteralPath "D:\WUJI\WindowsAgent") {
        return "D:\WUJI\WindowsAgent"
    }

    return Join-Path $env:LOCALAPPDATA "WUJI\WindowsAgent"
}

function Ensure-ObjectProperty {
    param(
        [pscustomobject]$Object,
        [string]$Name,
        [object]$Value
    )

    if ($Object.PSObject.Properties[$Name]) {
        $Object.$Name = $Value
        return
    }

    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
}

function Write-ReloadCommand {
    param(
        [string]$ControlPath,
        [string]$RequestedBy
    )

    $requestId = $RequestedBy + "-" + [guid]::NewGuid().ToString("N")
    [ordered]@{
        command = "ReloadConfig"
        requestId = $requestId
        requestedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        requestedBy = $RequestedBy
        waitForCompletion = $false
        timeoutMilliseconds = 5000
    } | ConvertTo-Json | Set-Content -LiteralPath $ControlPath -Encoding UTF8

    return $requestId
}

$AgentRoot = Get-AgentRoot $AgentRoot
$configDir = Join-Path $AgentRoot "config"
$runtimeDir = Join-Path $AgentRoot "runtime"
$optionsPath = Join-Path $configDir "windows-agent.json"
$backupPath = Join-Path $configDir "windows-agent.json.manual-privacy-timed-check.bak"
$controlPath = Join-Path $runtimeDir "agent_control.json"
$excludedProcess = "notepad"

New-Item -ItemType Directory -Force -Path $configDir | Out-Null
New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

$optionsExisted = Test-Path -LiteralPath $optionsPath
if ($optionsExisted) {
    Copy-Item -LiteralPath $optionsPath -Destination $backupPath -Force
    $options = Get-Content -LiteralPath $optionsPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    Set-Content -LiteralPath $backupPath -Value "{}" -Encoding UTF8
    $options = [pscustomobject]@{}
}

$processes = @()
if ($options.PSObject.Properties["excludedProcesses"] -and $null -ne $options.excludedProcesses) {
    $processes = @($options.excludedProcesses)
}

if ($processes -notcontains $excludedProcess) {
    $processes += $excludedProcess
}

Ensure-ObjectProperty $options "useMockCapture" $false
Ensure-ObjectProperty $options "samplingIntervalSeconds" 1
Ensure-ObjectProperty $options "heartbeatIntervalSeconds" 1
Ensure-ObjectProperty $options "maskWindowTitles" $true
Ensure-ObjectProperty $options "excludedProcesses" $processes

$options | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $optionsPath -Encoding UTF8

$reloadRequestId = Write-ReloadCommand -ControlPath $controlPath -RequestedBy "ManualPrivacyProcessTimedCheck"

Write-Host "Configured excluded process: $excludedProcess"
Write-Host "ReloadConfig requestId: $reloadRequestId"
Write-Host "Waiting $ReloadWaitSeconds seconds for Agent to reload config..."
Start-Sleep -Seconds $ReloadWaitSeconds

$testFile = Join-Path $env:TEMP "WUJI_PROCESS_PRIVACY_TIMED_CHECK.txt"
Set-Content -LiteralPath $testFile -Value "Keep this Notepad window in foreground for the timed process privacy check." -Encoding UTF8
Start-Process notepad.exe -ArgumentList "`"$testFile`""

Start-Sleep -Seconds 1
$startUtc = (Get-Date).ToUniversalTime()

Write-Host ""
Write-Host "STRICT_START_UTC:"
Write-Host $startUtc.ToString("O")
Write-Host ""
Write-Host "Keep the opened Notepad window in the foreground for $HoldSeconds seconds."
Start-Sleep -Seconds $HoldSeconds

$endUtc = (Get-Date).ToUniversalTime()
Write-Host ""
Write-Host "STRICT_END_UTC:"
Write-Host $endUtc.ToString("O")

if ($optionsExisted) {
    Copy-Item -LiteralPath $backupPath -Destination $optionsPath -Force
}
else {
    if (Test-Path -LiteralPath $optionsPath) {
        Remove-Item -LiteralPath $optionsPath -Force
    }
}

$restoreRequestId = Write-ReloadCommand -ControlPath $controlPath -RequestedBy "ManualPrivacyTimedRestore"

Write-Host ""
Write-Host "Restored windows-agent.json from backup."
Write-Host "Restore ReloadConfig requestId: $restoreRequestId"
Write-Host ""
Write-Host "Run these DB Browser queries with the strict window:"
Write-Host ""
Write-Host "SELECT COUNT(*) AS process_privacy_notepad_samples"
Write-Host "FROM foreground_samples"
Write-Host "WHERE sample_time_utc >= '$($startUtc.ToString("O"))'"
Write-Host "  AND sample_time_utc <= '$($endUtc.ToString("O"))'"
Write-Host "  AND lower(process_name) IN ('notepad', 'notepad.exe');"
Write-Host ""
Write-Host "SELECT id, event_time_utc, event_type, event_level, payload_json"
Write-Host "FROM agent_events"
Write-Host "WHERE event_time_utc >= '$($startUtc.ToString("O"))'"
Write-Host "  AND event_time_utc <= '$($endUtc.ToString("O"))'"
Write-Host "  AND event_type = 'PrivacyFiltered'"
Write-Host "ORDER BY id DESC;"
Write-Host ""
Write-Host "Expected:"
Write-Host "- process_privacy_notepad_samples = 0"
Write-Host "- PrivacyFiltered events have ruleType Process and processName Notepad"
Write-Host "- PrivacyFiltered count is at most 5 for this short run"
