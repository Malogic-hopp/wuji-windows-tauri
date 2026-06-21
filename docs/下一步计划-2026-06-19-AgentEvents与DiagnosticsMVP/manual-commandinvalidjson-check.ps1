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
$badPath = $controlPath + ".bad"

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

$rawJson = @"
{
  "command": "Pause",
  "requestId": "manual-invalid-json-should-not-appear",
  "rawSecret": "this raw JSON text should not appear in agent_events"
"@

Set-Content -LiteralPath $controlPath -Value $rawJson -Encoding UTF8

Write-Host "Wrote malformed JSON to:"
Write-Host $controlPath
Write-Host ""
Write-Host "Expected after the Agent next tick:"
Write-Host "- agent_control.json is moved to agent_control.json.bad"
Write-Host "- Diagnostics Recent Events shows CommandInvalidJson"
Write-Host "- event_level is Warning"
Write-Host "- errorCode is CommandInvalidJson"
Write-Host "- requestId is empty"
Write-Host "- payload only includes commandSource/FileFallback, quarantined=true, fileKind=agent_control"
Write-Host "- raw JSON text does not appear in message or payload"
Write-Host ""
Write-Host "Bad file path:"
Write-Host $badPath
