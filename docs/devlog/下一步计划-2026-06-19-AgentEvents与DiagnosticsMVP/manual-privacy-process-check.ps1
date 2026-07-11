param(
    [string]$AgentRoot
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

$AgentRoot = Get-AgentRoot $AgentRoot
$configDir = Join-Path $AgentRoot "config"
$runtimeDir = Join-Path $AgentRoot "runtime"
$optionsPath = Join-Path $configDir "windows-agent.json"
$backupPath = Join-Path $configDir "windows-agent.json.manual-privacy-check.bak"
$statePath = Join-Path $configDir "manual-privacy-check-state.json"
$controlPath = Join-Path $runtimeDir "agent_control.json"
$excludedProcess = "notepad"
$requestId = "manual-privacy-process-" + [guid]::NewGuid().ToString("N")

New-Item -ItemType Directory -Force -Path $configDir | Out-Null
New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

if (-not (Test-Path -LiteralPath $statePath)) {
    $existed = Test-Path -LiteralPath $optionsPath
    if ($existed) {
        Copy-Item -LiteralPath $optionsPath -Destination $backupPath -Force
    }
    else {
        Set-Content -LiteralPath $backupPath -Value "{}" -Encoding UTF8
    }

    [ordered]@{
        existed = $existed
        backupPath = $backupPath
    } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}

if (Test-Path -LiteralPath $optionsPath) {
    $options = Get-Content -LiteralPath $optionsPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
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

[ordered]@{
    command = "ReloadConfig"
    requestId = $requestId
    requestedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    requestedBy = "ManualPrivacyProcessCheck"
    waitForCompletion = $false
    timeoutMilliseconds = 5000
} | ConvertTo-Json | Set-Content -LiteralPath $controlPath -Encoding UTF8

$testFile = Join-Path $env:TEMP "WUJI_PROCESS_PRIVACY_CHECK.txt"
Set-Content -LiteralPath $testFile -Value "Keep this Notepad window in foreground for the process privacy check." -Encoding UTF8
Start-Process notepad.exe -ArgumentList "`"$testFile`""

Write-Host "Configured excluded process:"
Write-Host $excludedProcess
Write-Host ""
Write-Host "ReloadConfig requestId:"
Write-Host $requestId
Write-Host ""
Write-Host "A Notepad window was opened."
Write-Host "Keep it in the foreground for several sampling ticks, then refresh Diagnostics."
Write-Host ""
Write-Host "Expected:"
Write-Host "- Recent Events shows PrivacyFiltered"
Write-Host "- payload/reason is generic, such as process privacy rule"
Write-Host "- processName may show notepad/Notepad, but no title content should be present"
Write-Host "- foreground_samples should not grow while Notepad remains foreground"
Write-Host "- repeated hits should be rate-limited to at most 5 per 5 minutes for the same key"
