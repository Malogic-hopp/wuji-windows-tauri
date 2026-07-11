param(
    [string]$AgentRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($AgentRoot)) {
    if (Test-Path -LiteralPath "D:\WUJI\WindowsAgent") {
        $AgentRoot = "D:\WUJI\WindowsAgent"
    }
    else {
        $AgentRoot = Join-Path $env:LOCALAPPDATA "WUJI\WindowsAgent"
    }
}

$configDir = Join-Path $AgentRoot "config"
$runtimeDir = Join-Path $AgentRoot "runtime"
$optionsPath = Join-Path $configDir "windows-agent.json"
$statePath = Join-Path $configDir "manual-privacy-check-state.json"
$controlPath = Join-Path $runtimeDir "agent_control.json"
$requestId = "manual-privacy-restore-" + [guid]::NewGuid().ToString("N")

if (-not (Test-Path -LiteralPath $statePath)) {
    Write-Host "No manual privacy check backup state found:"
    Write-Host $statePath
    Write-Host "Nothing to restore."
    exit 0
}

$state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
$backupPath = [string]$state.backupPath

if (-not (Test-Path -LiteralPath $backupPath)) {
    throw "Backup file not found: $backupPath"
}

if ($state.existed -eq $true) {
    Copy-Item -LiteralPath $backupPath -Destination $optionsPath -Force
}
else {
    if (Test-Path -LiteralPath $optionsPath) {
        Remove-Item -LiteralPath $optionsPath -Force
    }
}

Remove-Item -LiteralPath $statePath -Force

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
[ordered]@{
    command = "ReloadConfig"
    requestId = $requestId
    requestedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    requestedBy = "ManualPrivacyRestore"
    waitForCompletion = $false
    timeoutMilliseconds = 5000
} | ConvertTo-Json | Set-Content -LiteralPath $controlPath -Encoding UTF8

Write-Host "Restored windows-agent.json from backup."
Write-Host "ReloadConfig requestId:"
Write-Host $requestId
