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
$pattern = "*WUJI_TITLE_PRIVACY_CHECK*"
$requestId = "manual-privacy-title-" + [guid]::NewGuid().ToString("N")

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

$patterns = @()
if ($options.PSObject.Properties["excludedTitlePatterns"] -and $null -ne $options.excludedTitlePatterns) {
    $patterns = @($options.excludedTitlePatterns)
}

if ($patterns -notcontains $pattern) {
    $patterns += $pattern
}

Ensure-ObjectProperty $options "useMockCapture" $false
Ensure-ObjectProperty $options "samplingIntervalSeconds" 1
Ensure-ObjectProperty $options "heartbeatIntervalSeconds" 1
Ensure-ObjectProperty $options "maskWindowTitles" $true
Ensure-ObjectProperty $options "excludedTitlePatterns" $patterns

$options | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $optionsPath -Encoding UTF8

[ordered]@{
    command = "ReloadConfig"
    requestId = $requestId
    requestedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    requestedBy = "ManualPrivacyTitleCheck"
    waitForCompletion = $false
    timeoutMilliseconds = 5000
} | ConvertTo-Json | Set-Content -LiteralPath $controlPath -Encoding UTF8

$testFile = Join-Path $env:TEMP "WUJI_TITLE_PRIVACY_CHECK.txt"
Set-Content -LiteralPath $testFile -Value "Keep this Notepad window in foreground for the title privacy check." -Encoding UTF8
Start-Process notepad.exe -ArgumentList "`"$testFile`""

Write-Host "Configured title privacy pattern:"
Write-Host $pattern
Write-Host ""
Write-Host "ReloadConfig requestId:"
Write-Host $requestId
Write-Host ""
Write-Host "A Notepad window was opened with a matching title."
Write-Host "Keep it in the foreground for several sampling ticks, then refresh Diagnostics."
Write-Host ""
Write-Host "Expected:"
Write-Host "- Recent Events shows PrivacyFiltered"
Write-Host "- payload/reason is generic, such as title privacy rule"
Write-Host "- the literal title WUJI_TITLE_PRIVACY_CHECK does not appear in the event"
Write-Host "- foreground_samples should not get a sample for this title hit"
