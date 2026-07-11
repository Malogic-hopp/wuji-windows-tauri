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

$runtimeDir = Join-Path $AgentRoot "runtime"
$controlPath = Join-Path $runtimeDir "agent_control.json"
$requestId = "manual-commandfailed-" + [guid]::NewGuid().ToString("N")

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

$command = [ordered]@{
    command = 999
    requestId = $requestId
    requestedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    requestedBy = "ManualDiagnosticsCheck"
    waitForCompletion = $false
    timeoutMilliseconds = 5000
}

$command | ConvertTo-Json | Set-Content -LiteralPath $controlPath -Encoding UTF8

Write-Host "Wrote unsupported command to:"
Write-Host $controlPath
Write-Host ""
Write-Host "RequestId:"
Write-Host $requestId
Write-Host ""
Write-Host "Expected Diagnostics events after the Agent next tick:"
Write-Host "- CommandDetected"
Write-Host "- CommandFailed"
Write-Host "- ErrorCode UnsupportedCommand"
Write-Host ""
Write-Host "CommandAccepted / CommandCompleted should not appear for this RequestId."
